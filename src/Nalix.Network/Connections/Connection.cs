// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Internal.Connections;
using Nalix.Network.Internal.Initialization;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Internal.Security;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

namespace Nalix.Network.Connections;

/// <summary>
/// Represents a network connection that manages socket communication, stream
/// transformation, and event handling.
/// This is the high-level owner for the socket transport and the per-connection
/// event pipeline.
/// </summary>
public sealed partial class Connection :
    IConnection,
    IConnectionErrorTracked,
    IConnectionTrafficMetrics,
    IPooledConnectContextPool,
    TimingWheel.ITimeoutTrackedConnection
{
    #region Fields

    private static readonly ObjectPoolManager s_pool;
    private static readonly TimingWheel s_timingWheel;

    private static readonly ConnectionGuardOptions s_options;
    private static readonly DatagramGuardOptions s_datagramOptions;
    private static readonly TimingWheelOptions s_timingWheelOptions;
    private static readonly NetworkCallbackOptions s_callbackOptions;

    private ConnectionBacking? _backing;

    private volatile bool _disposed;

    #endregion Fields

    #region Constructor

    static Connection()
    {
        // Ensure the static constructor is called before any instance is created
        // to initialize static fields and read configuration.

        s_pool = ObjectPoolManager.Shared;
        s_options = ConfigurationManager.Instance.Get<ConnectionGuardOptions>();
        s_timingWheel = InstanceManager.Instance.GetOrCreateInstance<TimingWheel>();
        s_datagramOptions = ConfigurationManager.Instance.Get<DatagramGuardOptions>();
        s_callbackOptions = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();
        s_timingWheelOptions = ConfigurationManager.Instance.Get<TimingWheelOptions>();

        NetworkPoolInitializer.InitializeTcp();
    }

    /// <summary>Initializes a new instance of the <see cref="Connection"/> class.</summary>
    /// <param name="socket">The connected socket used for the connection.</param>
    /// <param name="packetClassifier">The opcode extractor for classifying incoming packets.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> is null.</exception>
    public Connection(Socket socket, IOpCodeExtractor packetClassifier)
        : this(socket, packetClassifier, socket?.RemoteEndPoint ?? throw new InternalErrorException("Socket does not expose a remote endpoint."))
    {
    }

    /// <summary>
    /// Initializes a Connection with an overridden real endpoint (Proxy Protocol).
    /// Use this overload when the TCP peer is a proxy that injects a PROXY header.
    /// </summary>
    public Connection(Socket socket, IOpCodeExtractor packetClassifier, System.Net.EndPoint realEndPoint)
    {
        ArgumentNullException.ThrowIfNull(realEndPoint);
        ArgumentNullException.ThrowIfNull(packetClassifier);

        _disposed = false;

        _backing = s_pool.Get<ConnectionBacking>();
        _backing.Initialize();

        this.Secret = Bytes32.Zero;
        this.PacketClassifier = packetClassifier;
        // Snapshot the remote endpoint up front so the connection can be logged
        // and tracked even before protocol-level events begin.
        this.ConnectionId = Snowflake.NewId(SnowflakeType.Session).ToUInt64();

        // Use realEndPoint (from PROXY header) instead of socket.RemoteEndPoint (LB IP).
        this.NetworkEndpoint = SocketEndpoint.FromEndPoint(realEndPoint);
        this.IdleTimeoutMs = s_timingWheelOptions.IdleTimeoutMs;

        // Initialize the socket connection.
        _backing.Socket = s_pool.Get<SocketConnection>();
        _backing.Socket.Initialize(socket, this);
        _backing.TcpTransport = s_pool.Get<SocketTcpTransport>();
        _backing.TcpTransport.Initialize(this, _backing.Socket);

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.Connection:UnknownMethod", $"created remote=remote-endpoint={this.NetworkEndpoint} id=connection-id={this.ConnectionId:X16}"));
        }
    }

    #endregion Constructor

    #region Properties

    /// <inheritdoc/>
    public bool IsDisposed => _disposed || Volatile.Read(ref _backing) is null;

    /// <inheritdoc/>
    public bool IsUdpCreated => this.UdpTransport is not null;

    /// <inheritdoc/>
    public bool ExcludeFromIdleTimeout { get; set; }

    /// <inheritdoc />
    public ulong ConnectionId { get; }

    /// <inheritdoc />
    public string? UserId { get; set; }

    /// <inheritdoc/>
    public IConnection.ITransport TCP => Volatile.Read(ref _backing)?.TcpTransport ?? throw new ObjectDisposedException(nameof(Connection));

    /// <inheritdoc/>
    public IConnection.ITransport? UDP => Volatile.Read(ref _backing)?.UdpTransport;

    /// <inheritdoc/>
    public IOpCodeExtractor PacketClassifier { get; }

    /// <inheritdoc />
    public INetworkEndpoint NetworkEndpoint { get; }

    /// <inheritdoc />
    public IObjectMap<AttributeKey, object> Attributes
    {
        get
        {
            ConnectionBacking backing = Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(Connection));
            return backing.Attributes ??= ObjectMap<AttributeKey, object>.Rent();
        }
    }

    /// <inheritdoc />
    public ConcurrentDictionary<ushort, object> RateLimitCache
    {
        get
        {
            ConnectionBacking backing = Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(Connection));
            return backing.RateLimitCache ??= new();
        }
    }

    /// <inheritdoc />
    public int ErrorCount => Volatile.Read(ref _backing)?.ErrorCount ?? 0;

    /// <inheritdoc />
    public long UpTime { get => (long)Clock.UnixTime().TotalMilliseconds - field; } = (long)Clock.UnixTime().TotalMilliseconds;

    /// <inheritdoc />
    public long LastPingTime => Volatile.Read(ref _backing)?.Socket?.LastPingTime ?? 0;

    /// <summary>
    /// Returns the number of packets currently pending in the async callback pipeline.
    /// </summary>
    public int PendingPackets => Volatile.Read(ref _backing)?.PendingProcessCallbacks ?? 0;

    /// <inheritdoc />
    public PermissionLevel Level { get; set; } = PermissionLevel.NONE;

    /// <inheritdoc />
    public CipherSuiteType Algorithm { get; set; } = CipherSuiteType.Chacha20Poly1305;

    /// <inheritdoc />
    public Bytes32 Secret { get; set; }

    /// <inheritdoc />
    public int IdleTimeoutMs { get; set; }

    /// <inheritdoc />
    public int TimeoutVersion { get; set; }

    /// <inheritdoc />
    public bool IsRegisteredInWheel { get; set; }

    /// <summary>
    /// Tracks the current timeout task in the TimingWheel.
    /// Used for manual reference breaking during Dispose to allow instant GC.
    /// </summary>
    TimingWheel.TimeoutTask? TimingWheel.ITimeoutTrackedConnection.TimeoutTask { get; set; }

    /// <summary>
    /// Gets the total number of bytes sent over the life of the connection.
    /// </summary>
    /// <remarks>
    /// This value combines data from the underlying <see cref="SocketConnection.BytesSent"/> (TCP)
    /// and the <see cref="SocketUdpTransport.BytesSent"/> (UDP) if available.
    /// It represents raw wire data, including protocol headers.
    /// </remarks>
    public long BytesSent
    {
        get
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            if (backing == null)
            {
                return 0;
            }

            return (backing.Socket?.BytesSent ?? 0)
                 + (backing.UdpTransport?.BytesSent ?? 0)
                 + Volatile.Read(ref backing.BytesSent);
        }
    }

    /// <summary>
    /// Gets the total number of bytes received over the life of the connection.
    /// </summary>
    /// <remarks>
    /// This value combines data from the underlying <see cref="SocketConnection.BytesReceived"/> (TCP)
    /// and the <see cref="SocketUdpTransport.BytesReceived"/> (UDP) if available.
    /// It represents raw wire data before any frame processing or decompression.
    /// </remarks>
    public long BytesReceived
    {
        get
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            if (backing == null)
            {
                return 0;
            }

            return (backing.Socket?.BytesReceived ?? 0)
                 + (backing.UdpTransport?.BytesReceived ?? 0)
                 + Volatile.Read(ref backing.BytesReceived);
        }
    }

    /// <inheritdoc />
    public long PacketsDropped => Volatile.Read(ref _backing)?.PacketsDropped ?? 0;

    /// <inheritdoc />
    public void IncrementPacketsDropped()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Increment(ref backing.PacketsDropped);
        }
    }

    #endregion Properties

    #region Internal

    internal SocketUdpTransport? UdpTransport
    {
        get => Volatile.Read(ref _backing)?.UdpTransport;
        private set
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            _ = backing?.UdpTransport = value;
        }
    }

    internal SlidingWindow UdpReplayWindow
    {
        get
        {
            ConnectionBacking backing = Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(Connection));
            return backing.UdpReplayWindow ??= new(s_datagramOptions.UdpReplayWindowSize);
        }
    }

#if DEBUG
    /// <summary>
    /// Injects a packet directly into the process pipeline for testing.
    /// This bypasses the socket receive loop but still triggers AsyncCallback
    /// and respects the per-connection throttle.
    /// </summary>
    internal void InjectIncoming(Environment.Memory.BufferLease lease)
    {
        ConnectionEventArgs? args = this.AcquireEventArgs();

        if (args == null)
        {
            return;
        }

        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Increment(ref backing.PendingProcessCallbacks);
        }

        args.Initialize(lease, this);

        if (!Internal.Transport.AsyncCallback.Invoke(MessageProcessingBridge, this, args, CallbackLane.Process, releasePendingPacketOnCompletion: true))
        {
            ((IPooledConnectContextPool)this).ReleasePendingPacket();
            _ = args.ExchangeLease(null);
            args.Dispose();
            lease.Dispose();
        }
    }
#endif

    internal void SetUdpTransport(SocketUdpTransport transport) => this.UdpTransport = transport;

    internal void InjectPreReadBytes(ReadOnlySpan<byte> preReadData) => Volatile.Read(ref _backing)?.Socket?.InjectPreReadBytes(preReadData);

    #endregion Internal

    #region Events

    /// <inheritdoc />

    public event EventHandler<IConnectionEventArgs> ConnectionClosed
    {
        add
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            _ = backing?.ConnectionClosed += value;
        }
        remove
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            _ = backing?.ConnectionClosed -= value;
        }
    }

    /// <inheritdoc />
    public event EventHandler<IConnectionEventArgs> MessageProcessed
    {
        add
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            _ = backing?.MessageProcessed += value;
        }
        remove
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            _ = backing?.MessageProcessed -= value;
        }
    }

    /// <inheritdoc />
    public event EventHandler<IConnectionEventArgs> MessageProcessing
    {
        add
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            _ = backing?.MessageProcessing += value;
        }
        remove
        {
            ConnectionBacking? backing = Volatile.Read(ref _backing);
            _ = backing?.MessageProcessing -= value;
        }
    }

    #endregion Events

    #region Methods

    /// <inheritdoc />
    public void IncrementErrorCount()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null)
        {
            return;
        }

        int count = Interlocked.Increment(ref backing.ErrorCount);

        // SEC-54: Disconnect persistent noisy/malformed connections
        if (s_options.MaxErrorThreshold > 0 && count >= s_options.MaxErrorThreshold)
        {
            this.Disconnect("Exceeded maximum error threshold.");
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementBytesSent(int bytes)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Add(ref backing.BytesSent, bytes);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementBytesReceived(int bytes)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Add(ref backing.BytesReceived, bytes);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Disconnect(string? reason = null)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.Connection:Disconnect", $"disconnect request id={this.ConnectionId} remote={this.NetworkEndpoint} reason={reason}"));
        }

        if (_disposed)
        {
            return;
        }

        this.Dispose();
    }

    /// <inheritdoc />
    public void UpdateIdleTimeout(int newTimeoutMs)
    {
        if (newTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newTimeoutMs), "Idle timeout must be a positive integer.");
        }

        if (this.IdleTimeoutMs == newTimeoutMs)
        {
            return; // No change needed
        }

        this.IdleTimeoutMs = newTimeoutMs;

        s_timingWheel.Unregister(this);
        s_timingWheel.Register(this);
    }

    #endregion Methods

    #region Dispose Pattern

    /// <inheritdoc />
    public void Dispose()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null)
        {
            return;
        }

        // Guard against recursive calls or concurrent disposal.
        // Only the first thread that moves state from 0 to 1 gets to trigger the events.
        int previousState = Interlocked.CompareExchange(ref backing.DisposeState, 1, 0);

        if (previousState == 0)
        {
            // We are the primary disposer.
            bool signaledHere = false;
            try
            {
                // Signal that we are closing but NOT yet fully disposed.
                // This allows event handlers (like session persistence) to still read attributes.
                if (Interlocked.Exchange(ref backing.CloseSignaled, 1) == 0)
                {
                    signaledHere = true;
                    if (backing.ConnectionClosed != null)
                    {
                        ConnectionEventArgs args = s_pool.Get<ConnectionEventArgs>();
                        args.Initialize(this);

                        try
                        {
                            Delegate[] handlers = backing.ConnectionClosed.GetInvocationList();
                            for (int i = 0; i < handlers.Length; i++)
                            {
                                EventHandler<IConnectionEventArgs> handler = (EventHandler<IConnectionEventArgs>)handlers[i];
                                try
                                {
                                    handler(this, args);
                                }
                                catch (Exception handlerEx) when (ExceptionClassifier.IsNonFatal(handlerEx))
                                {
                                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                                    {
                                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.Connection:Dispose", "close-handler-error", handlerEx));
                                    }
                                }
                            }
                        }
                        finally
                        {
                            args.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.Connection:Dispose", "close-event-error", ex));
                }
            }
            finally
            {
                // Now that all handlers have finished, we can proceed to the destructive phase.
                // But only if we are the ones who signaled the close AND there is no bridge dispatch running.
                // If a bridge dispatch is running, it will handle cleanup in its own finally block.
                if (signaledHere && Volatile.Read(ref backing.IsDispatchingClose) == 0)
                {
                    this.PerformDestructiveCleanup();
                }
            }
            return;
        }

        // If we are already in state 1 (Closing) or 2 (Disposed), we just return.
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816:Dispose methods should call SuppressFinalize", Justification = "<Pending>")]
    private void PerformDestructiveCleanup()
    {
        _disposed = true;
        ConnectionBacking? backing = Interlocked.Exchange(ref _backing, null);
        if (backing == null)
        {
            return;
        }

        lock (backing.Lock)
        {
            if (Volatile.Read(ref backing.DisposeState) == 2)
            {
                return;
            }

            // Important: we don't set _disposed = true until the end,
            // but we must mark state as 2 immediately to prevent concurrent cleanup.
            Volatile.Write(ref backing.DisposeState, 2);
        }

        try
        {
            this.Secret = Bytes32.Zero;

            try
            {
                // Return pooled metadata first so the connection does not keep
                // borrowed state alive after disposal begins.
                backing.Attributes?.Return();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { LOG_ERROR(ex, "attributes"); }
            backing.Attributes = null;

            // High-Performance Cleanup: Break the TimingWheel reference chain instantly.
            // This allows the GC to collect the Connection immediately instead of 
            // waiting for the 102s wheel rotation.
            TimingWheel.TimeoutTask? task = ((TimingWheel.ITimeoutTrackedConnection)this).TimeoutTask;
            if (task is not null)
            {
                task.Conn = null;
                ((TimingWheel.ITimeoutTrackedConnection)this).TimeoutTask = null;
            }

            try { backing.Socket?.Dispose(); }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { LOG_ERROR(ex, "socket"); }

            try
            {
                if (backing.UdpTransport != null)
                {
                    s_pool.Return(backing.UdpTransport);
                    backing.UdpTransport = null;
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { LOG_ERROR(ex, "udptransport"); }

            try
            {
                if (backing.TcpTransport != null)
                {
                    backing.TcpTransport.Dispose();
                    s_pool.Return(backing.TcpTransport);
                    backing.TcpTransport = null;
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { LOG_ERROR(ex, "tcptransport"); }

            // DO NOT call backing.ArgsPool.Destroy() or backing.ContextPool.Destroy()
            // in order to safely pool the local pool for reuse.
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            LOG_ERROR(ex, "general");
        }
        finally
        {
            _disposed = true;
            s_pool.Return(backing);
        }

        GC.SuppressFinalize(this);

        static void LOG_ERROR(Exception ex, string component)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.Connection:Internal", $"component={component}-dispose-error", ex));
            }
        }
    }

    #endregion Dispose Pattern

    #region Internal Pooling

    /// <summary>
    /// Acquires an EventArgs from the connection's local pool for packet processing.
    /// Returns null if the local pool is exhausted (throttle reached).
    /// </summary>
    internal ConnectionEventArgs AcquireEventArgs()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        ConnectionEventArgs? arg_local = backing?.ArgsPool.Acquire(this, static (arg, self) => arg.Initialize(self));
        if (arg_local != null)
        {
            return arg_local;
        }

        ConnectionEventArgs? arg_global = s_pool.Get<ConnectionEventArgs>();
        arg_global.Initialize(this);

        return arg_global;
    }

    internal void ReturnEventArgs(ConnectionEventArgs args)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            backing.ArgsPool.Return(args);
        }
        else
        {
            s_pool.Return(args);
        }
    }

    /// <summary>
    /// Acquires a transition context from the connection's local pool.
    /// Used by AsyncCallback to execute packet handoffs without global pooling.
    /// </summary>
    PooledConnectEventContext IPooledConnectContextPool.AcquireContext()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        PooledConnectEventContext? arg_local = backing?.ContextPool.Acquire(this, static (ctx, self) => ctx.LocalOwner = self);
        if (arg_local != null)
        {
            arg_local.LocalOwner = this;
            return arg_local;
        }

        PooledConnectEventContext? arg_global = s_pool.Get<PooledConnectEventContext>();
        arg_global.LocalOwner = this;

        return arg_global;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IPooledConnectContextPool.ReleasePendingPacket()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Decrement(ref backing.PendingProcessCallbacks);
        }
    }

    void IPooledConnectContextPool.ReturnContext(PooledConnectEventContext context)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);

        if (backing != null)
        {
            backing.ContextPool.Return(context);
        }
        else
        {
            s_pool.Return(context);
        }
    }

    #endregion Internal Pooling

    #region SocketConnection Callbacks

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal bool OnFrameReceived(BufferLease lease)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null)
        {
            return false;
        }

        // Fast path: a non-blocking frame processor (the default protocol over the queued
        // dispatcher) runs directly on the receive completion. It only decodes and enqueues into
        // the dispatch channel, which is the real hand-off to worker threads, so the extra
        // thread-pool hop bought no isolation. Per-connection pending limits do not apply because
        // nothing is left pending once the call returns.
        if (Internal.Transport.AsyncCallback.CanRunInline(backing.MessageProcessing))
        {
            ConnectionEventArgs inlineArgs = this.AcquireEventArgs();
            inlineArgs.Initialize(lease, this);
            Internal.Transport.AsyncCallback.InvokeInline(MessageProcessingBridge, this, inlineArgs);
            return true;
        }

        int pending = Interlocked.Increment(ref backing.PendingProcessCallbacks);

        if (pending > s_callbackOptions.MaxPerConnectionPendingPackets)
        {
            _ = Interlocked.Decrement(ref backing.PendingProcessCallbacks);

            if (s_callbackOptions.OverflowPolicy == NetworkOverflowPolicy.Disconnect)
            {
                this.Disconnect("Exceeded MaxPerConnectionPendingPackets threshold.");
            }

            return false;
        }

        ConnectionEventArgs args = this.AcquireEventArgs();
        args.Initialize(lease, this);

        if (!Internal.Transport.AsyncCallback.Invoke(MessageProcessingBridge, this, args, CallbackLane.Process, releasePendingPacketOnCompletion: true))
        {
            _ = Interlocked.Decrement(ref backing.PendingProcessCallbacks);
            _ = args.ExchangeLease(null);
            args.Dispose();
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void OnFrameSent()
    {
        // Only pay for event args + a thread-pool hop when someone listens to MessageProcessed.
        if (Volatile.Read(ref _backing)?.MessageProcessed is null)
        {
            return;
        }

        ConnectionEventArgs args = this.AcquireEventArgs();
        args.Initialize(this);

        if (!Internal.Transport.AsyncCallback.Invoke(MessageProcessedBridge, this, args, CallbackLane.Post))
        {
            args.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void OnTransportClosed()
    {
        ConnectionEventArgs args = s_pool.Get<ConnectionEventArgs>();
        args.Initialize(this);

        if (!Internal.Transport.AsyncCallback.InvokeHighPriority(this.ConnectionClosedBridge, this, args))
        {
            args.Dispose();
        }
    }

    #endregion SocketConnection Callbacks

    #region Event Bridges

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ConnectionClosedBridge(object? sender, IConnectionEventArgs e)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null || Interlocked.Exchange(ref backing.CloseSignaled, 1) != 0)
        {
            e.Dispose();
            return;
        }

        // Close events bypass backpressure because cleanup must never be delayed.
        if (!Internal.Transport.AsyncCallback.InvokeHighPriority(ConnectionClosedDispatchBridge, this, e))
        {
            e.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void MessageProcessingBridge(object? sender, IConnectionEventArgs e)
    {
        if (e is null)
        {
            return;
        }

        if (sender is not Connection self)
        {
            e.Dispose();
            return;
        }

        SAFE_PROCESS_EVENT_BRIDGE(self, e);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SAFE_PROCESS_EVENT_BRIDGE(Connection self, IConnectionEventArgs e)
    {
        try
        {
            ConnectionBacking? backing = Volatile.Read(ref self._backing);
            backing?.MessageProcessing?.Invoke(self, e);
        }
        finally
        {
            e.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void MessageProcessedBridge(object? sender, IConnectionEventArgs e)
    {
        if (e is null)
        {
            return;
        }

        if (sender is not Connection self)
        {
            e.Dispose();
            return;
        }

        SAFE_POST_PROCESS_EVENT_BRIDGE(self, e);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SAFE_POST_PROCESS_EVENT_BRIDGE(Connection self, IConnectionEventArgs e)
    {
        try
        {
            ConnectionBacking? backing = Volatile.Read(ref self._backing);
            backing?.MessageProcessed?.Invoke(self, e);
        }
        finally
        {
            e.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void ConnectionClosedDispatchBridge(object? sender, IConnectionEventArgs e)
    {
        if (e is null || sender is not Connection self)
        {
            e?.Dispose();
            return;
        }

        SAFE_CLOSE_EVENT_DISPATCH_BRIDGE(self, e);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SAFE_CLOSE_EVENT_DISPATCH_BRIDGE(Connection self, IConnectionEventArgs e)
    {
        try
        {
            ConnectionBacking? backing = Volatile.Read(ref self._backing);
            if (backing == null)
            {
                return;
            }

            _ = Interlocked.Exchange(ref backing.IsDispatchingClose, 1);
            if (backing.ConnectionClosed != null)
            {
                Delegate[] handlers = backing.ConnectionClosed.GetInvocationList();
                for (int i = 0; i < handlers.Length; i++)
                {
                    EventHandler<IConnectionEventArgs> handler = (EventHandler<IConnectionEventArgs>)handlers[i];
                    try
                    {
                        handler(self, e);
                    }
                    catch (Exception handlerEx) when (ExceptionClassifier.IsNonFatal(handlerEx))
                    {
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.Connection:Internal", "close-handler-error", handlerEx));
                        }
                    }
                }
            }
        }
        finally
        {
            ConnectionBacking? backing = Volatile.Read(ref self._backing);
            if (backing != null)
            {
                _ = Interlocked.Exchange(ref backing.IsDispatchingClose, 0);
            }
            e.Dispose();

            // If the socket signaled the close (via bridge) and Dispose() was never called
            // by the user, OR if it was called but skipped cleanup because it saw
            // the bridge was already signaled, we ensure cleanup happens here.
            if (backing != null && Volatile.Read(ref backing.DisposeState) != 2)
            {
                self.PerformDestructiveCleanup();
            }
        }
    }

    #endregion Event Bridges
}
