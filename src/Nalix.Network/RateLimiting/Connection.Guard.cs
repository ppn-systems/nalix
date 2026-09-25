// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Time;
using Nalix.Network.Internal.Security;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

namespace Nalix.Network.RateLimiting;

/// <summary>
/// High-performance per-endpoint concurrent connection limiter.
/// Uses a hybrid approach: a sealed class entry (<see cref="ConnectionLimitEntry"/>) holds
/// a mutable <see cref="ConnectionLimitInfo"/> struct protected by a per-entry lock,
/// plus a <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> for rate-window tracking.
/// Supports automatic cleanup of stale entries to bound memory usage.
/// </summary>
[Injectable]
[SkipLocalsInit]
[DebuggerNonUserCode]
[Injectable(typeof(IConnectionGuard))]
[Injectable(typeof(IProofOfWorkPolicy))]
public sealed partial class ConnectionGuard : IConnectionGuard, IProofOfWorkPolicy
{
    #region Constants

    private const double EwmaAlpha = 0.3;
    private const int MinReportCapacity = 128;
    private const int MaxReportCapacity = 4096;

    #endregion Constants

    #region Fields

    private readonly ConnectionQuotaOptions _config;
    private readonly TrustedProxyOptions _proxyConfig;
    private readonly ConnectionGuardOptions _protectionConfig;

    private readonly long _windowTicks;
    private readonly int _maxPerEndpoint;
    private readonly int _maxGlobalConnections;
    private readonly int _maxTrackedEndpoints;
    private readonly long _logSuppressWindowTicks;

    private readonly TimeSpan _cleanupInterval;
    private readonly TimeSpan _inactivityThreshold;
    private readonly NetworkAccessList _accessList;
    private readonly NetworkBanRepository _banRepository;
    private readonly ConcurrentDictionary<SocketEndpoint, ConnectionLimitEntry> _map;

    private int _disposed;
    private int _reloadPending;
    private IRecurringHandle? _saveJob;
    private IRecurringHandle? _cleanupJob;
    private IRecurringHandle? _ewmaJob;
    private IRecurringHandle? _hotReloadJob;
    private System.IO.FileSystemWatcher? _configWatcher;

    /// <summary>
    /// Metrics for monitoring
    /// </summary>
    /// <summary>
    /// Metrics for monitoring
    /// </summary>
    private int _globalConnections;
    private long _totalConnectionAttempts;
    private long _totalRejections;
    private long _totalCleanedEntries;

    private int _peakGlobalConnections;
    private int _peakTrackedEndpoints;

    private double _ewmaConnectionRate;
    private long _ewmaLastUpdateTicks;
    private long _ewmaLastConnectionAttempts;
    private SpinLock _ewmaLock = new(false);

    private readonly long _banCountDecayWindowTicks;

    #endregion Fields

    #region Properties

    /// <inheritdoc/>
    public byte CurrentDifficulty
    {
        get
        {
            if (!_config.EnableAdaptiveMode)
            {
                return _config.AdaptivePowMinDifficulty;
            }

            // Rate-based scale (0..1)
            double rate = Volatile.Read(ref _ewmaConnectionRate);
            double rateScale;
            if (rate <= _config.AdaptivePowStartRate)
            {
                rateScale = 0.0;
            }
            else if (rate >= _config.AdaptivePowMaxRate)
            {
                rateScale = 1.0;
            }
            else
            {
                rateScale = (rate - _config.AdaptivePowStartRate) / (_config.AdaptivePowMaxRate - _config.AdaptivePowStartRate);
            }

            // Capacity-based scale (0..1)
            double capacityScale = 1.0;
            if (_config.EnableCapacityBasedPoW && _maxGlobalConnections > 0)
            {
                double loadFactor = (double)Volatile.Read(ref _globalConnections) / _maxGlobalConnections;
                if (loadFactor <= _config.CapacityPoWThreshold)
                {
                    capacityScale = 0.0;
                }
                else
                {
                    capacityScale = (loadFactor - _config.CapacityPoWThreshold) / (1.0 - _config.CapacityPoWThreshold);
                }
            }

            double finalScale = rateScale * capacityScale;
            double diffRange = _config.AdaptivePowMaxDifficulty - _config.AdaptivePowMinDifficulty;
            return (byte)(_config.AdaptivePowMinDifficulty + (diffRange * finalScale));
        }
    }

    /// <summary>
    /// Indicates whether the server is currently under attack based on the elevated Proof-of-Work difficulty.
    /// </summary>
    public bool IsUnderAttack => this.CurrentDifficulty > _config.AdaptivePowMinDifficulty;

    /// <summary>Gets the recurring name used for cleanup operations.</summary>
    public static readonly string RecurringName;

    #endregion Properties

    #region Constructors

    static ConnectionGuard() => RecurringName = "conn.limit";

    /// <summary>
    /// Initializes a new <see cref="ConnectionGuard"/> with optional configuration.
    /// </summary>
    /// <param name="config">Configuration options. If null, uses global configuration.</param>
    /// <exception cref="InternalErrorException">Thrown when configuration validation fails.</exception>
    public ConnectionGuard(ConnectionQuotaOptions? config = null)
    {
        _proxyConfig = ConfigurationManager.Instance.Get<TrustedProxyOptions>();
        _protectionConfig = ConfigurationManager.Instance.Get<ConnectionGuardOptions>();
        _config = config ?? ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();

        _config.Validate();
        _proxyConfig.Validate();
        _protectionConfig.Validate();

        _cleanupInterval = _config.CleanupInterval;
        _inactivityThreshold = _config.InactivityThreshold;

        _windowTicks = _config.ConnectionRateWindow.Ticks;
        _maxPerEndpoint = _config.MaxConnectionsPerIpAddress;
        _maxGlobalConnections = _protectionConfig.MaxConnections;
        _maxTrackedEndpoints = _config.MaxTrackedEndpoints;
        _logSuppressWindowTicks = _protectionConfig.DDoSLogSuppressWindow.Ticks;

        _banRepository = new NetworkBanRepository();
        _accessList = new NetworkAccessList(_proxyConfig);
        _map = new System.Collections.Concurrent.ConcurrentDictionary<SocketEndpoint, ConnectionLimitEntry>();

        _banCountDecayWindowTicks = ConfigurationManager.Instance.Get<ConnectionBanStoreOptions>().BanCountDecayWindow.Ticks;

        _banRepository.Load(_map);

        this.INITIALIZE_METRICS();
        this.SCHEDULE_CLEANUP_JOB();
        this.INITIALIZE_HOT_RELOAD();

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            string inactivityVal = _inactivityThreshold.ToString(null, CultureInfo.InvariantCulture);
            string cleanupVal = _cleanupInterval.ToString(null, CultureInfo.InvariantCulture);
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.ConnectionGuard:Internal", $"init max-per-endpoint={_maxPerEndpoint.ToString(CultureInfo.InvariantCulture)} inactivity={inactivityVal} cleanup={cleanupVal}"));
        }
    }


    /// <summary>Initializes a new <see cref="ConnectionGuard"/> using global configuration.</summary>
    public ConnectionGuard() : this(config: null) { }

    #endregion Constructors

    #region Public API

    /// <summary>
    /// Safely decrements the connection counter without requiring an IConnection.
    /// Used for rollback when connection initialization fails after TryAccept succeeded.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Release(IPEndPoint endPoint)
    {
        if (Volatile.Read(ref _disposed) != 0 || endPoint?.Address is null)
        {
            return;
        }

        if (_maxGlobalConnections > -1)
        {
            _ = Interlocked.Decrement(ref _globalConnections);
        }

        DateTime now = Clock.NowUtc();
        SocketEndpoint key = CONVERT_TO_NETWORK_ENDPOINT(endPoint);
        _ = this.TRY_RELEASE_CONNECTION_SLOT(key, now);

        // Zero-alloc subnet release: extract subnet key from raw bytes.
        if (key.TryGetSubnetKey(out uint ipv4Key, out long ipv6Key, out bool isV6))
        {
            if (isV6)
            {
                if (_subnetMapV6.TryGetValue(ipv6Key, out SubnetLimitEntry? entry6) && entry6 is not null)
                {
                    this.RELEASE_SUBNET_ENTRY(entry6, now);
                }
            }
            else
            {
                if (_subnetMapV4.TryGetValue(ipv4Key, out SubnetLimitEntry? entry4) && entry4 is not null)
                {
                    this.RELEASE_SUBNET_ENTRY(entry4, now);
                }
            }
        }
    }

    /// <summary>
    /// Manually bans an IP address for a specified duration.
    /// This bypasses progressive limits and applies the ban immediately.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BanEndpoint(IPAddress address, TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(ConnectionGuard));
        ArgumentNullException.ThrowIfNull(address);

        if (_accessList.IsTrustedProxy(address))
        {
            return;
        }

        SocketEndpoint key = SocketEndpoint.FromIpAddress(address);
        long nowTicks = Clock.NowUtc().Ticks;
        long banUntilTicks = nowTicks + duration.Ticks;

        // Get the entry or create a new one if the IP address has never connected.
        ConnectionLimitEntry entry = _map.GetOrAdd(key, static _ => new ConnectionLimitEntry());

        // Update ban duration under lock to ensure BannedUntilTicks and LastBanTimeTicks are written atomically
        bool lockTaken = false;
        try
        {
            entry.SpinLock.Enter(ref lockTaken);
            entry.BannedUntilTicks = banUntilTicks;
            entry.LastBanTimeTicks = nowTicks;
        }
        finally
        {
            if (lockTaken)
            {
                entry.SpinLock.Exit();
            }
        }

        // Mark as dirty so NetworkBanRepository saves it to a file in the next cycle
        _banRepository.MarkDirty();

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Security.Banned))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Security.Banned, new DiagnosticLog("NW.ConnectionGuard:BanEndpoint", $" address={address} banned-until={new DateTime(banUntilTicks, DateTimeKind.Utc)}"));
        }
    }

    /// <summary>
    /// Attempts to acquire a connection slot for the given endpoint.
    /// </summary>
    /// <param name="endPoint">The IP endpoint requesting connection.</param>
    /// <returns>True if connection is allowed; false if limit exceeded.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if limiter is disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown if IPEndPoint is null.</exception>"
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryAccept(IPEndPoint endPoint)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(ConnectionGuard));
        ArgumentNullException.ThrowIfNull(endPoint);

        _ = Interlocked.Increment(ref _totalConnectionAttempts);

        DateTime now = Clock.NowUtc();

        // 1. Invalid endpoint -> Reject
        if (endPoint.Address is null || endPoint.Address.Equals(IPAddress.Any) || endPoint.Address.Equals(IPAddress.IPv6Any))
        {
            _ = Interlocked.Increment(ref _totalRejections);
            return false;
        }

        // 2. Blacklist -> Reject
        if (_accessList.IsBlacklisted(endPoint.Address))
        {
            _ = Interlocked.Increment(ref _totalRejections);
            return false;
        }

        if (_maxGlobalConnections > -1)
        {
            while (true)
            {
                int current = Volatile.Read(ref _globalConnections);
                if (current >= _maxGlobalConnections)
                {
                    _ = Interlocked.Increment(ref _totalRejections);
                    return false;
                }
                if (Interlocked.CompareExchange(ref _globalConnections, current + 1, current) == current)
                {
                    int newTotal = current + 1;
                    if (newTotal > Volatile.Read(ref _peakGlobalConnections))
                    {
                        int currentPeak;
                        while (newTotal > (currentPeak = Volatile.Read(ref _peakGlobalConnections)))
                        {
                            if (Interlocked.CompareExchange(ref _peakGlobalConnections, newTotal, currentPeak) == currentPeak)
                            {
                                break;
                            }
                        }
                    }
                    break;
                }
            }
        }

        SocketEndpoint key = CONVERT_TO_NETWORK_ENDPOINT(endPoint);

        long attempts = Interlocked.Read(ref _totalConnectionAttempts);
        if (attempts % 100 == 0)
        {
            this.UPDATE_EWMA_SHARED();
        }

        ConnectionAllowResult result = this.TRY_ACQUIRE_CONNECTION_SLOT(key, now, endPoint.Address);

        if (result.Allowed && !this.IS_EXEMPT_LOOPBACK(key))
        {
            SubnetAllowResult subnetResult = this.TRY_ACQUIRE_SUBNET_SLOT(endPoint.Address, now.Ticks);
            if (!subnetResult.Allowed)
            {
                _ = this.TRY_RELEASE_CONNECTION_SLOT(key, now);
                if (_maxGlobalConnections > -1)
                {
                    _ = Interlocked.Decrement(ref _globalConnections);
                }
                _ = Interlocked.Increment(ref _totalRejections);
                return false;
            }
        }

        if (!result.Allowed)
        {
            if (_maxGlobalConnections > -1)
            {
                _ = Interlocked.Decrement(ref _globalConnections);
            }

            _ = Interlocked.Increment(ref _totalRejections);

            // Throttled reject log — chỉ log 1 lần mỗi suppress window per IP
            if (_map.TryGetValue(key, out ConnectionLimitEntry? entry) && entry is not null)
            {
                long nowTicks = now.Ticks;
                long windowTicks = _logSuppressWindowTicks;

                if (ThrottledEventGate.TryAcquire(
                        ref entry.LastRejectLogTicks,
                        ref entry.SuppressedRejectCount,
                        nowTicks, windowTicks,
                        out long suppressed))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Connections.Rejected))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Connections.Rejected, new DiagnosticLog("NW.ConnectionGuard:TryAccept", $" endpoint={endPoint} current={result.CurrentConnections} limit={_maxPerEndpoint} suppressed-count={suppressed}"));
                    }
                }
            }
        }
        else
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.ConnectionGuard:TryAccept", $"allow endpoint endpoint={endPoint} current={result.CurrentConnections} limit={_maxPerEndpoint}"));
            }
        }

        return result.Allowed;
    }

    /// <summary>
    /// Zero-allocation overload that accepts a <see cref="SocketEndpoint"/> directly.
    /// Fully zero-alloc on the reject path: no IPAddress, no IPEndPoint, no string.
    /// Used by the PROXY v2 parser which returns SocketEndpoint from raw bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal bool TryAccept(SocketEndpoint endpoint)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(ConnectionGuard));

        if (endpoint == SocketEndpoint.Empty)
        {
            _ = Interlocked.Increment(ref _totalRejections);
            return false;
        }

        _ = Interlocked.Increment(ref _totalConnectionAttempts);
        DateTime now = Clock.NowUtc();

        // Zero-alloc blacklist check: uses HashSet<SocketEndpoint> + raw-byte CIDR matching.
        if (_accessList.IsBlacklisted(endpoint))
        {
            _ = Interlocked.Increment(ref _totalRejections);
            return false;
        }

        if (_maxGlobalConnections > -1)
        {
            while (true)
            {
                int current = Volatile.Read(ref _globalConnections);
                if (current >= _maxGlobalConnections)
                {
                    _ = Interlocked.Increment(ref _totalRejections);
                    return false;
                }
                if (Interlocked.CompareExchange(ref _globalConnections, current + 1, current) == current)
                {
                    int newTotal = current + 1;
                    if (newTotal > Volatile.Read(ref _peakGlobalConnections))
                    {
                        int currentPeak;
                        while (newTotal > (currentPeak = Volatile.Read(ref _peakGlobalConnections)))
                        {
                            if (Interlocked.CompareExchange(ref _peakGlobalConnections, newTotal, currentPeak) == currentPeak)
                            {
                                break;
                            }
                        }
                    }
                    break;
                }
            }
        }

        long attempts = Interlocked.Read(ref _totalConnectionAttempts);
        if (attempts % 100 == 0)
        {
            this.UPDATE_EWMA_SHARED();
        }

        // Zero-alloc connection slot: trusted-proxy check uses SocketEndpoint directly.
        ConnectionAllowResult result = this.TRY_ACQUIRE_CONNECTION_SLOT(endpoint, now);

        if (result.Allowed && !this.IS_EXEMPT_LOOPBACK(endpoint))
        {
            // Zero-alloc subnet slot: extract subnet key from raw bytes via TryGetSubnetKey.
            SubnetAllowResult subnetResult = this.TRY_ACQUIRE_SUBNET_SLOT(endpoint, now.Ticks);
            if (!subnetResult.Allowed)
            {
                _ = this.TRY_RELEASE_CONNECTION_SLOT(endpoint, now);
                if (_maxGlobalConnections > -1)
                {
                    _ = Interlocked.Decrement(ref _globalConnections);
                }
                _ = Interlocked.Increment(ref _totalRejections);
                return false;
            }
        }

        if (!result.Allowed)
        {
            if (_maxGlobalConnections > -1)
            {
                _ = Interlocked.Decrement(ref _globalConnections);
            }

            _ = Interlocked.Increment(ref _totalRejections);

            if (_map.TryGetValue(endpoint, out ConnectionLimitEntry? entry) && entry is not null)
            {
                long nowTicks = now.Ticks;
                long windowTicks = _logSuppressWindowTicks;

                if (ThrottledEventGate.TryAcquire(
                        ref entry.LastRejectLogTicks,
                        ref entry.SuppressedRejectCount,
                        nowTicks, windowTicks,
                        out long suppressed))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Connections.Rejected))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Connections.Rejected, new DiagnosticLog("NW.ConnectionGuard:TryAccept", $" endpoint={FormatEndpointForLog(endpoint)} current={result.CurrentConnections} limit={_maxPerEndpoint} suppressed-count={suppressed}"));
                    }
                }
            }
        }
        else
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.ConnectionGuard:TryAccept", $"allow endpoint endpoint={FormatEndpointForLog(endpoint)} current={result.CurrentConnections} limit={_maxPerEndpoint}"));
            }
        }

        return result.Allowed;
    }

    /// <summary>
    /// Handles connection closure event and decrements the connection counter.
    /// </summary>
    /// <param name="sender">Event sender (unused).</param>
    /// <param name="args">Connection event arguments.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Because it's required by the event signature")]
    public void OnConnectionClosed(object? sender, IConnectionEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (args?.Connection?.NetworkEndpoint is null)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.ConnectionGuard:OnConnectionClosed", "received-null args/connection/endpoint"));
            }
            return;
        }

        // Zero-alloc empty check: compare struct directly instead of materializing Address string.
        SocketEndpoint key = SocketEndpoint.FromNetworkEndpoint(args.Connection.NetworkEndpoint);
        if (key == SocketEndpoint.Empty)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.ConnectionGuard:OnConnectionClosed", "received-empty-address"));
            }
            return;
        }

        if (_maxGlobalConnections > -1)
        {
            _ = Interlocked.Decrement(ref _globalConnections);
        }

        DateTime now = Clock.NowUtc();
        bool released = this.TRY_RELEASE_CONNECTION_SLOT(key, now);

        // Zero-alloc subnet release: extract subnet key from raw bytes instead of
        // materializing key.Address -> string -> IPAddress.TryParse -> IPAddress.
        if (key.TryGetSubnetKey(out uint ipv4Key, out long ipv6Key, out bool isV6))
        {
            if (isV6)
            {
                if (_subnetMapV6.TryGetValue(ipv6Key, out SubnetLimitEntry? entry6) && entry6 is not null)
                {
                    this.RELEASE_SUBNET_ENTRY(entry6, now);
                }
            }
            else
            {
                if (_subnetMapV4.TryGetValue(ipv4Key, out SubnetLimitEntry? entry4) && entry4 is not null)
                {
                    this.RELEASE_SUBNET_ENTRY(entry4, now);
                }
            }
        }

        if (released && _map.TryGetValue(key, out ConnectionLimitEntry? closedEntry) && closedEntry is not null)
        {
            long nowTicks = now.Ticks;

            if (args.Connection.UpTime < _config.ShortLivedThresholdMs && _config.ShortLivedThresholdMs > 0)
            {
                bool slLockTaken = false;
                try
                {
                    closedEntry.SpinLock.Enter(ref slLockTaken);
                    closedEntry.RecentConnectionTimestamps.Enqueue(nowTicks);
                }
                finally
                {
                    if (slLockTaken)
                    {
                        closedEntry.SpinLock.Exit();
                    }
                }
            }

            long windowTicks = _logSuppressWindowTicks;

            if (ThrottledEventGate.TryAcquire(
                    ref closedEntry.LastClosedLogTicks,
                    ref closedEntry.SuppressedClosedCount,
                    nowTicks, windowTicks,
                    out long suppressed))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Connections.Closed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Connections.Closed, new DiagnosticLog("NW.ConnectionGuard:OnConnectionClosed", $" endpoint={FormatEndpointForLog(key)} suppressed-count={suppressed}"));
                }
            }
        }
    }

    /// <summary>
    /// Checks if the provided endpoint belongs to a known trusted proxy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsTrustedProxy(IPEndPoint? endPoint) => endPoint?.Address != null && _accessList.IsTrustedProxy(endPoint.Address);

    #endregion Public API

    #region IDisposable & IAsyncDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _cleanupJob?.Dispose();
            _saveJob?.Dispose();
            _ewmaJob?.Dispose();
            _hotReloadJob?.Dispose();

            if (_configWatcher != null)
            {
                _configWatcher.EnableRaisingEvents = false;
                _configWatcher.Dispose();
            }

            if (_banRepository.IsEnabled)
            {
                _banRepository.Save(_map); // Save snapshot BEFORE clearing the map
            }

            _map.Clear();

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.ConnectionGuard:Dispose", "ConnectionGuard disposed"));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Critical))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Critical, new DiagnosticLog("NW.ConnectionGuard:Dispose", "ConnectionGuard dispose-error", ex));
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        this.Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion IDisposable & IAsyncDisposable
}
