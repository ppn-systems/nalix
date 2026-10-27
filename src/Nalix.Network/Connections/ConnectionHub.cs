// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Environment.Time;
using Nalix.Framework.Injection;
using Nalix.Framework.Tasks;
using Nalix.Network.Internal.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable CA2213 // Disposable fields should be disposed

namespace Nalix.Network.Connections;

/// <summary>
/// Manages connections for servers, optimized for high performance and thread safety.
/// </summary>
/// <remarks>
/// This class provides efficient connection management with minimal allocations and fast lookup operations.
/// It is thread-safe and uses concurrent collections to handle multiple connections simultaneously.
/// </remarks>
[SkipLocalsInit]
[DebuggerNonUserCode]
[DebuggerDisplay("ConnectionHub (Count={_count})")]
public sealed partial class ConnectionHub : IConnectionHub
{
    #region Fields

    private readonly int _shardCount;

    private readonly TimingWheel _timing;
    private readonly ConnectionRegistry _registry;
    private readonly ConnectionHubOptions _options;

    /// <summary>
    /// Connections statistics for monitoring
    /// </summary>
    private volatile bool _disposed;

    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _totalPacketsDropped;

    private long _ingressBytesPerSecond;
    private long _egressBytesPerSecond;

    private long _lastTotalBytesSentSnapshot;
    private long _lastTotalBytesReceivedSnapshot;
    private long _lastTotalPacketsDroppedSnapshot;

    private readonly IRecurringHandle? _throughputTask;

    /// <summary>
    /// Outbound-allocated collections for bulk operations
    /// </summary>
    private static readonly ArrayPool<IConnection> s_connectionPool;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets the recurring name used for buffer trimming operations.
    /// This value is embedded in the recurring job name so trimming jobs from
    /// different manager instances remain distinct.
    /// </summary>
    public static readonly string RecurringName;

    /// <summary>
    /// Gets the current number of active connections.
    /// </summary>
    public int Count => _registry.Count;

    /// <summary>
    /// Raised after a connection is successfully unregistered.
    /// </summary>
    public event Action<IConnection>? ConnectionUnregistered;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Initializes static members of the <see cref="ConnectionHub"/> class.
    /// </summary>
    static ConnectionHub()
    {
        RecurringName = "hub.throughput";
        s_connectionPool = ArrayPool<IConnection>.Shared;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionHub"/> class.
    /// </summary>

    public ConnectionHub()
    {
        _options = ConfigurationManager.Instance.Get<ConnectionHubOptions>();
        _options.Validate();

        /*
         * [Sharding Logic]
         * We shard the connections across multiple ConcurrentDictionaries.
         * This significantly reduces contention on the internal dictionary 
         * locks when many connections are being registered/unregistered 
         * simultaneously on a high-core machine.
         */
        _shardCount = Math.Max(1, _options.ShardCount);
        _registry = new ConnectionRegistry(_shardCount, 31);

        _throughputTask = InstanceManager.Instance.GetOrCreateInstance<TaskManager>()
                                                  .ScheduleRecurring(TaskNaming.Recurring
                                                  .CleanupJobId(RecurringName, this.GetHashCode()), TimeSpan.FromSeconds(1), this.CalculateThroughputAsync);

        _timing = InstanceManager.Instance.GetOrCreateInstance<TimingWheel>();
    }

    #endregion Constructor

    #region APIs

    /// <inheritdoc />
    /// <summary>
    /// Registers a new connection with the hub.
    /// </summary>
    /// <param name="connection">The connection to register.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="connection"/> is null.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RegisterConnection(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        RegisterResult result = this.TryRegisterCore(connection);
        if (result == RegisterResult.Success)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(result == RegisterResult.Disposed, nameof(ConnectionHub));

        if (result == RegisterResult.Duplicate)
        {
            throw new InternalErrorException($"Connection '{connection.ConnectionId}' is already registered.");
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Unregisters a connection from the hub.
    /// </summary>
    /// <param name="connection">The connection to unregister.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void UnregisterConnection(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _ = this.TryUnregisterCore(connection);
    }


    /// <inheritdoc />
    /// <summary>
    /// Retrieves a connection by its identifier.
    /// </summary>
    /// <param name="id">The identifier of the connection to retrieve.</param>
    /// <returns>The connection associated with the identifier, or <c>null</c> if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public IConnection? GetConnection(ulong id) => _registry.GetConnection(id);

    /// <summary>
    /// Retrieves a connection by its serialized identifier.
    /// </summary>
    /// <param name="id">The serialized identifier of the connection.</param>
    /// <returns>The connection associated with the identifier, or <c>null</c> if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: MaybeNull]
    public IConnection? GetConnection(ReadOnlySpan<byte> id)
    {
        ulong key = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(id);
        return _registry.GetConnection(key);
    }

    /// <inheritdoc />
    /// <summary>
    /// Retrieves a read-only collection of all active connections.
    /// </summary>
    /// <returns>A read-only collection of active connections.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [SuppressMessage("Style", "IDE0301:Simplify collection initialization", Justification = "<Pending>")]
    public IReadOnlyCollection<IConnection> ListConnections()
    {
        if (_disposed || _registry.Count == 0)
        {
            return Array.Empty<IConnection>();
        }

        return _registry.CaptureConnectionSnapshot();
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public IReadOnlyCollection<IConnection> ListConnections(INetworkEndpoint networkEndpoint)
    {
        ArgumentNullException.ThrowIfNull(networkEndpoint);

        if (_disposed || _registry.Count == 0)
        {
            return Array.Empty<IConnection>();
        }

        return _registry.CaptureConnectionSnapshot(networkEndpoint);
    }

    /// <inheritdoc />
    /// <summary>
    /// Releases all resources used by the <see cref="ConnectionHub"/> and closes all connections.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _throughputTask?.Dispose();
        this.DisposeAllConnections();

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog("NW.ConnectionHub:Dispose", "disposed"));
        }
    }

    #endregion APIs

    #region IReportable

    /// <summary>
    /// Generates a human-readable report of active connections and statistics.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public string GenerateReport()
    {
        long sumBytesSent = Volatile.Read(ref _totalBytesSent);
        long sumBytesReceived = Volatile.Read(ref _totalBytesReceived);
        long sumPacketsDropped = Volatile.Read(ref _totalPacketsDropped);
        long sumUptime = 0, maxUptime = 0, minUptime = long.MaxValue;

        StringBuilder sb = new(1024);
        Dictionary<string, int> algoCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> statusCounts = new(StringComparer.OrdinalIgnoreCase);

        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] ConnectionHub Status:");
        int total = _registry.Count;
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Connections    : {total}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Shard Count          : {_shardCount}");

        foreach (ConcurrentDictionary<ulong, IConnection> shard in _registry.Shards)
        {
            foreach (IConnection conn in shard.Values)
            {
                if (conn is IConnectionTrafficMetrics metrics)
                {
                    sumBytesSent += metrics.BytesSent;
                    sumBytesReceived += metrics.BytesReceived;
                    sumPacketsDropped += metrics.PacketsDropped;
                }

                long up = conn.UpTime;

                sumUptime += up;

                if (up > maxUptime)
                {
                    maxUptime = up;
                }

                if (up < minUptime)
                {
                    minUptime = up;
                }

                string status = conn.Level.ToString();
                string algo = conn.Algorithm.ToString();
                algoCounts[algo] = algoCounts.TryGetValue(algo, out int n) ? n + 1 : 1;
                statusCounts[status] = statusCounts.TryGetValue(status, out int cur) ? cur + 1 : 1;
            }
        }

        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Bytes Sent   : {sumBytesSent:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Bytes Received : {sumBytesReceived:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Packets Dropped: {sumPacketsDropped:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Ingress Bps          : {Volatile.Read(ref _ingressBytesPerSecond):N0} B/s");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Egress Bps           : {Volatile.Read(ref _egressBytesPerSecond):N0} B/s");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Average Uptime     : {(total > 0 ? sumUptime / total : 0)}s");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Max Connection Time: {maxUptime}s");
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"Min Connection Time: {(minUptime == long.MaxValue ? 0 : minUptime)}s");

        _ = sb.AppendLine();
        _ = sb.AppendLine("Connection Status Summary:");
        _ = sb.AppendLine("----------------------------------------");
        _ = sb.AppendLine("Status          | Count");
        _ = sb.AppendLine("----------------------------------------");
        foreach (KeyValuePair<string, int> kvp in statusCounts)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{kvp.Key,-15} | {kvp.Value,5}");
        }

        // Include AsyncCallback dispatcher metrics
        Internal.Transport.AsyncCallbackMetrics callbackStats = Internal.Transport.AsyncCallback.GetStatistics();
        NetworkCallbackOptions netOpts = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();
        _ = sb.AppendLine();
        _ = sb.AppendLine("AsyncCallback Queue Dispatcher Stats:");
        _ = sb.AppendLine("----------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Pending Process Callbacks : {callbackStats.PendingProcess:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Pending Post Callbacks    : {callbackStats.PendingPost:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Callbacks Invoked   : {callbackStats.Total:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Dropped Callbacks         : {callbackStats.Dropped:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Slow Callbacks (> {netOpts.MaxCallbackExecutionMs}ms) : {callbackStats.SlowCallbacks:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Fairness Map Collisions   : {callbackStats.FairnessCollisions:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Fairness Map Evictions    : {callbackStats.FairnessEvictions:N0}");

        Internal.Time.TimingWheelMetrics wheelStats = _timing.GetStatistics();
        _ = sb.AppendLine();
        _ = sb.AppendLine("TimingWheel Idle Timeout Stats:");
        _ = sb.AppendLine("----------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Registered Connections    : {wheelStats.RegisteredCount:N0} (Peak: {wheelStats.PeakRegistered:N0})");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Timeouts            : {wheelStats.TotalTimeouts:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Registrations       : {wheelStats.TotalRegistrations:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Unregistrations     : {wheelStats.TotalUnregistrations:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Stale Skips         : {wheelStats.TotalStaleSkips:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Max Tick Drift            : {wheelStats.MaxTickDrift:N0} ticks");

        return sb.ToString();
    }

    /// <inheritdoc/>
    public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        int total = _registry.Count;

        writer.WriteStartObject();
        writer.WriteString("UtcNow", DateTime.UtcNow);
        writer.WriteNumber("TotalConnections", total);
        writer.WriteNumber("ShardCount", _shardCount);
        writer.WriteNumber("TotalBytesSent", Volatile.Read(ref _lastTotalBytesSentSnapshot));
        writer.WriteNumber("TotalBytesReceived", Volatile.Read(ref _lastTotalBytesReceivedSnapshot));
        writer.WriteNumber("TotalPacketsDropped", Volatile.Read(ref _lastTotalPacketsDroppedSnapshot));
        writer.WriteNumber("IngressBytesPerSecond", Volatile.Read(ref _ingressBytesPerSecond));
        writer.WriteNumber("EgressBytesPerSecond", Volatile.Read(ref _egressBytesPerSecond));

        // Write AsyncCallback metrics
        AsyncCallbackMetrics callbackStats = Internal.Transport.AsyncCallback.GetStatistics();
        writer.WriteStartObject("AsyncCallback");
        writer.WriteNumber("PendingProcess", callbackStats.PendingProcess);
        writer.WriteNumber("PendingPost", callbackStats.PendingPost);
        writer.WriteNumber("TotalInvoked", callbackStats.Total);
        writer.WriteNumber("Dropped", callbackStats.Dropped);
        writer.WriteNumber("SlowCallbacks", callbackStats.SlowCallbacks);
        writer.WriteNumber("FairnessCollisions", callbackStats.FairnessCollisions);
        writer.WriteNumber("FairnessEvictions", callbackStats.FairnessEvictions);
        writer.WriteEndObject();

        Internal.Time.TimingWheelMetrics wheelStats = _timing.GetStatistics();
        writer.WriteStartObject("TimingWheel");
        writer.WriteNumber("RegisteredCount", wheelStats.RegisteredCount);
        writer.WriteNumber("PeakRegistered", wheelStats.PeakRegistered);
        writer.WriteNumber("TotalRegistrations", wheelStats.TotalRegistrations);
        writer.WriteNumber("TotalUnregistrations", wheelStats.TotalUnregistrations);
        writer.WriteNumber("TotalTimeouts", wheelStats.TotalTimeouts);
        writer.WriteNumber("TotalStaleSkips", wheelStats.TotalStaleSkips);
        writer.WriteNumber("MaxTickDrift", wheelStats.MaxTickDrift);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    #endregion IReportable

    #region Private Methods

    // Sharding logic moved to ConnectionRegistry

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging", Justification = "<Pending>")]
    private RegisterResult TryRegisterCore(IConnection connection)
    {
        if (_disposed)
        {
            return RegisterResult.Disposed;
        }

        bool measureLatency = _options.IsEnableLatency && DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information);
        TimingScope scope = measureLatency ? TimingScope.Start() : default;

        ulong connectionKey = connection.ConnectionId;

        connection.ConnectionClosed += this.OnClientDisconnected;
        connection.Attributes[ConnectionAttributes.OwnerHub] = this;

        bool added = false;
        try
        {
            if (!_registry.TryAdd(connectionKey, connection))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(
                        DiagnosticsEvents.Internal.Debug,
                        new DiagnosticLog("NW.ConnectionHub:RegisterConnection", $"register-dup id={connection.ConnectionId:X16}"));
                }

                return RegisterResult.Duplicate;
            }

            added = true;

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.ConnectionHub:RegisterConnection", $"register id={connection.ConnectionId:X16} total={_registry.Count}"));
            }

            // The close may have been signaled before our ConnectionClosed handler was added (the handler list
            // is snapshotted at signal time), in which case nothing would ever unregister this connection.
            // The handler is added before TryAdd and the flag is read after it, so a close signaled later
            // is guaranteed to reach OnClientDisconnected.
            if (connection is Connection concrete && concrete.IsCloseSignaled)
            {
                _ = this.TryUnregisterCore(connection);
            }

            if (measureLatency)
            {
                string latency = scope.GetElapsedMilliseconds().ToString("0.000", CultureInfo.InvariantCulture);
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Information,
                    new DiagnosticLog("PERF.NW.ConnectionHub:RegisterConnection", $"id={connection.ConnectionId:X16}, latency={latency} ms"));
            }

            return RegisterResult.Success;
        }
        finally
        {
            if (!added)
            {
                connection.ConnectionClosed -= this.OnClientDisconnected;
                _ = connection.Attributes.Remove(ConnectionAttributes.OwnerHub);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging", Justification = "<Pending>")]
    private bool TryUnregisterCore(IConnection connection)
    {
        ulong connectionKey = connection.ConnectionId;

#pragma warning disable CA2000 // Dispose objects before losing scope
        if (!_registry.TryRemove(connectionKey, out IConnection? existing) || existing is null)
#pragma warning restore CA2000 // Dispose objects before losing scope
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Debug,
                    new DiagnosticLog("NW.ConnectionHub:UnregisterConnection", $"unregister-miss id={connection.ConnectionId:X16}"));
            }

            return false;
        }

        bool measureLatency = _options.IsEnableLatency && DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information);
        TimingScope scope = measureLatency ? TimingScope.Start() : default;

        IConnection removedConnection = existing ?? connection;
        removedConnection.ConnectionClosed -= this.OnClientDisconnected;

        try
        {
            ConnectionUnregistered?.Invoke(removedConnection);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog("NW.ConnectionHub:UnregisterConnection", $"event-error id={removedConnection.ConnectionId:X16}", ex));
            }
        }

        try
        {
            if (connection is IConnectionTrafficMetrics metrics)
            {
                _ = Interlocked.Add(ref _totalBytesSent, metrics.BytesSent);
                _ = Interlocked.Add(ref _totalBytesReceived, metrics.BytesReceived);
                _ = Interlocked.Add(ref _totalPacketsDropped, metrics.PacketsDropped);
            }

            removedConnection.Dispose();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog("NW.ConnectionHub:UnregisterConnection", $"dispose-error id={removedConnection.ConnectionId:X16}", ex));
            }
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Trace,
                new DiagnosticLog("NW.ConnectionHub:UnregisterConnection", $"unregister id={removedConnection.ConnectionId:X16} total={_registry.Count}"));
        }

        if (measureLatency)
        {
            string latency = scope.GetElapsedMilliseconds().ToString("0.000", CultureInfo.InvariantCulture);
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Information,
                new DiagnosticLog("PERF.NW.ConnectionHub:UnregisterConnection", $"id={removedConnection.ConnectionId:X16}, latency={latency} ms"));
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void DisposeAllConnections()
    {
        using RentedConnectionSnapshot snapshot = _registry.CaptureConnectionSnapshotRented();

        for (int i = 0; i < snapshot.Count; i++)
        {
            snapshot[i].ConnectionClosed -= this.OnClientDisconnected;
        }

        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = _options.ParallelDisconnectDegree
        };

        // Copy to a local array for Parallel.ForEach (it requires IEnumerable)
        IConnection[] connections = new IConnection[snapshot.Count];
        snapshot.Span.CopyTo(connections);

        _ = Parallel.ForEach(connections, parallelOptions, connection =>
        {
            try
            {
                connection.Dispose();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    DiagnosticsEvents.Write(
                        DiagnosticsEvents.Internal.Error,
                        new DiagnosticLog("NW.ConnectionHub:DisposeAllConnections", $"dispose-error id={connection.ConnectionId:X16}", ex));
                }
            }
        });

        _registry.Clear();
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnClientDisconnected(object? sender, IConnectionEventArgs args)
    {
        if (args is null)
        {
            return;
        }

        _ = this.TryUnregisterCore(args.Connection);
    }

    private enum RegisterResult : byte
    {
        Success = 0,
        Disposed = 1,
        Duplicate = 2
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ValueTask CalculateThroughputAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        long sumBytesSent = Volatile.Read(ref _totalBytesSent);
        long sumBytesReceived = Volatile.Read(ref _totalBytesReceived);
        long sumPacketsDropped = Volatile.Read(ref _totalPacketsDropped);

        foreach (ConcurrentDictionary<ulong, IConnection> shard in _registry.Shards)
        {
            foreach (IConnection conn in shard.Values)
            {
                if (conn is not IConnectionTrafficMetrics metrics)
                {
                    continue;
                }

                long sent = metrics.BytesSent;
                long received = metrics.BytesReceived;
                long dropped = metrics.PacketsDropped;

                sumBytesSent += sent;
                sumBytesReceived += received;
                sumPacketsDropped += dropped;
            }
        }

        long lastSent = Volatile.Read(ref _lastTotalBytesSentSnapshot);
        long lastReceived = Volatile.Read(ref _lastTotalBytesReceivedSnapshot);

        if (lastSent > 0 || lastReceived > 0)
        {
            Volatile.Write(ref _egressBytesPerSecond, Math.Max(0, sumBytesSent - lastSent));
            Volatile.Write(ref _ingressBytesPerSecond, Math.Max(0, sumBytesReceived - lastReceived));
        }

        Volatile.Write(ref _lastTotalBytesSentSnapshot, sumBytesSent);
        Volatile.Write(ref _lastTotalBytesReceivedSnapshot, sumBytesReceived);
        Volatile.Write(ref _lastTotalPacketsDroppedSnapshot, sumPacketsDropped);

        return ValueTask.CompletedTask;
    }

    #endregion Private Methods
}

