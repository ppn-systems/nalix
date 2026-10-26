// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Internal.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

#pragma warning disable IDE0060 // Parameters required by IConnection interface
#pragma warning disable CA1822 // Interface members cannot be static
#pragma warning disable CA2213 // _udpTransport is returned to pool, not disposed directly

namespace Nalix.Network.Connections;

/// <summary>
/// A minimal <see cref="IConnection"/> for UDP passthrough mode.
/// Carries no TCP transport and no event pipeline — exists only so that
/// <see cref="IProtocol.ProcessMessage"/> receives a valid connection with
/// a UDP transport for sending replies.
/// </summary>
/// <remarks>
/// <para>
/// Not registered in <see cref="IConnectionHub"/> or TimingWheel.
/// Keyed by remote UDP endpoint inside <c>UdpPassthroughListener</c>.
/// </para>
/// <para>
/// <b>Safety note:</b> UDP is connectionless. Protocols using this mode must
/// implement their own session/auth layer (e.g., RakNet for Minecraft Bedrock).
/// </para>
/// </remarks>
public sealed class PassthroughConnection :
    IConnection,
    IConnectionTrafficMetrics,
    TimingWheel.ITimeoutTrackedConnection
{
    #region Constants

    /// <summary>
    /// Maximum number of packets allowed to be pending concurrently per connection.
    /// Prevents a single endpoint from monopolizing ThreadPool threads.
    /// </summary>
    internal const int MaxPendingPackets = 64;

    #endregion Constants

    #region Fields

    private static readonly ObjectPoolManager s_pool;

    private static readonly TimingWheelOptions s_timingWheelOptions;

    private readonly long _createdAtMs;
    private readonly EndPoint _endPointKey;
    private readonly TimingWheel _timingWheel;

    private SocketUdpTransport? _udpTransport;
    private IObjectMap<AttributeKey, object>? _attributes;
    private EventHandler<IConnectionEventArgs>? _connectionClosed;

    private int _isDisposed;
    private long _lastPingTime;
    private int _pendingPackets;

    #endregion Fields

    #region Constructor

    static PassthroughConnection()
    {
        s_pool = ObjectPoolManager.Shared;

        s_timingWheelOptions = ConfigurationManager.Instance.Get<TimingWheelOptions>();
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PassthroughConnection"/>.
    /// </summary>
    /// <param name="packetClassifier">The opcode extractor for classifying incoming packets.</param>
    /// <param name="remoteEndPoint">The remote UDP endpoint (must be an <see cref="IPEndPoint"/>).</param>

    public PassthroughConnection(IOpCodeExtractor packetClassifier, EndPoint remoteEndPoint)
        : this(packetClassifier, remoteEndPoint, timingWheel: null)
    {
    }

    internal PassthroughConnection(IOpCodeExtractor packetClassifier, EndPoint remoteEndPoint, TimingWheel? timingWheel)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(packetClassifier);

        _timingWheel = timingWheel ?? InstanceManager.Instance.GetOrCreateInstance<TimingWheel>();

        _endPointKey = remoteEndPoint;
        _lastPingTime = _createdAtMs = Clock.UnixMillisecondsNow();

        this.ExcludeFromIdleTimeout = false;
        this.PacketClassifier = packetClassifier;
        this.IdleTimeoutMs = s_timingWheelOptions.IdleTimeoutMs;
        this.ConnectionId = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
        this.NetworkEndpoint = SocketEndpoint.FromEndPoint(remoteEndPoint as IPEndPoint);
    }

    #endregion Constructor

    #region UDP Transport

    /// <summary>
    /// Creates and attaches a <see cref="SocketUdpTransport"/> using the shared
    /// listener socket. No-op if a transport is already attached or connection is disposed.
    /// </summary>
    /// <param name="listenerSocket">The shared UDP listener socket.</param>
    /// <param name="remoteEndPoint">The remote endpoint to send replies to.</param>
    internal void BindUdp(Socket listenerSocket, IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(listenerSocket);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        if (_udpTransport is not null || this.IsDisposed)
        {
            return;
        }

        SocketUdpTransport transport = s_pool.Get<SocketUdpTransport>();

        transport.SetSocket(listenerSocket);

        IPEndPoint ep = remoteEndPoint;
        transport.Initialize(ref ep);

        _udpTransport = transport;
    }

    #endregion UDP Transport

    #region Pending Packet Throttle

    /// <summary>
    /// Attempts to reserve a pending-packet slot for this connection.
    /// Returns <c>false</c> if the connection is throttled.
    /// </summary>
    internal bool TryAcquirePendingPacket()
    {
        while (true)
        {
            int current = Volatile.Read(ref _pendingPackets);
            if (current >= MaxPendingPackets)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _pendingPackets, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Releases a previously acquired pending-packet slot.
    /// </summary>
    internal void ReleasePendingPacket() => _ = Interlocked.Decrement(ref _pendingPackets);

    /// <summary>
    /// Gets the number of packets currently pending processing for this connection.
    /// </summary>
    internal int PendingPackets => Volatile.Read(ref _pendingPackets);

    #endregion Pending Packet Throttle

    #region IConnection Properties

    /// <inheritdoc />
    public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    /// <inheritdoc />
    public bool IsUdpCreated => _udpTransport is not null;

    /// <inheritdoc />
    public bool ExcludeFromIdleTimeout { get; set; }

    /// <inheritdoc />
    public ulong ConnectionId { get; }

    /// <inheritdoc />
    public string? UserId { get; set; }

    /// <inheritdoc />
    public long UpTime => Clock.UnixMillisecondsNow() - _createdAtMs;

    /// <inheritdoc />
    public long LastPingTime
    {
        get => Volatile.Read(ref _lastPingTime);
        set => Volatile.Write(ref _lastPingTime, value);
    }

    /// <inheritdoc/>
    public IOpCodeExtractor PacketClassifier { get; }

    /// <inheritdoc />
    public INetworkEndpoint NetworkEndpoint { get; }

    /// <inheritdoc />
    public IObjectMap<AttributeKey, object> Attributes => ConnectionAttributeMaps.GetOrCreate(ref _attributes);

    /// <inheritdoc />
    public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();

    /// <inheritdoc />
    public int ErrorCount { get; private set; }

    /// <inheritdoc />
    public PermissionLevel Level { get; set; } = PermissionLevel.NONE;

    /// <inheritdoc />
    public CipherSuiteType Algorithm { get; set; } = CipherSuiteType.Chacha20Poly1305;

    /// <inheritdoc />
    public Bytes32 Secret { get; set; }

    /// <inheritdoc />
    public long BytesSent => _udpTransport?.BytesSent ?? 0;

    /// <inheritdoc />
    public long BytesReceived => _udpTransport?.BytesReceived ?? 0;

    /// <inheritdoc />
    public long PacketsDropped => 0;

    #endregion IConnection Properties

    #region ITimeoutTrackedConnection

    /// <inheritdoc />
    public int IdleTimeoutMs { get; set; }

    /// <inheritdoc />
    public int TimeoutVersion { get; set; }

    /// <inheritdoc />
    public bool IsRegisteredInWheel { get; set; }

    /// <inheritdoc />
    TimingWheel.TimeoutTask? TimingWheel.ITimeoutTrackedConnection.TimeoutTask { get; set; }

    #endregion ITimeoutTrackedConnection

    #region TCP — Not Supported

    /// <inheritdoc />
    public IConnection.ITransport TCP => throw new NotSupportedException("Passthrough connections have no TCP transport.");

    #endregion TCP — Not Supported

    #region UDP

    /// <inheritdoc />
    public IConnection.ITransport UDP
    {
        get
        {
            if (_udpTransport is null)
            {
                Internal.Throw.UdpTransportNotCreated();
            }
            return _udpTransport;
        }
    }

    #endregion UDP

    #region IConnection Methods

    /// <summary>
    /// Gets the original <see cref="EndPoint"/> used as the dictionary key in the listener.
    /// Used by UdpPassthroughListener to remove the connection from its tracking map.
    /// </summary>
    internal EndPoint EndPointKey => _endPointKey;

    /// <inheritdoc />
    public void IncrementErrorCount() => this.ErrorCount++;

    /// <inheritdoc />
    public void IncrementBytesSent(int bytes) { }

    /// <inheritdoc />
    public void IncrementBytesReceived(int bytes) { }

    /// <inheritdoc />
    public void IncrementPacketsDropped() { }

    /// <inheritdoc />
    public void Disconnect(string? reason = null) => this.Dispose();

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

        _timingWheel.Unregister(this);
        _timingWheel.Register(this);
    }

    #endregion IConnection Methods

    #region Events

    /// <inheritdoc />
    public event EventHandler<IConnectionEventArgs>? ConnectionClosed
    {
        add => _connectionClosed += value;
        remove => _connectionClosed -= value;
    }

    /// <inheritdoc />
    public event EventHandler<IConnectionEventArgs>? MessageProcessed { add { } remove { } }

    /// <inheritdoc />
    public event EventHandler<IConnectionEventArgs>? MessageProcessing { add { } remove { } }

    #endregion Events

    #region Dispose

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        // Fire ConnectionClosed so TimingWheel and ConnectionGuard can clean up.
        try
        {
            if (_connectionClosed != null)
            {
                ConnectionEventArgs args = new();
                args.Initialize(this);

                try
                {
                    Delegate[] handlers = _connectionClosed.GetInvocationList();
                    for (int i = 0; i < handlers.Length; i++)
                    {
                        EventHandler<IConnectionEventArgs> handler = (EventHandler<IConnectionEventArgs>)handlers[i];
                        try
                        {
                            handler(this, args);
                        }
                        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                        {
                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                            {
                                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.PassthroughConnection:Dispose", "close-handler-error", ex));
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
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.PassthroughConnection:Dispose", "close-event-error", ex));
            }
        }

        // Break TimingWheel reference chain for instant GC.
        if (this is TimingWheel.ITimeoutTrackedConnection tracked)
        {
            TimingWheel.TimeoutTask? task = tracked.TimeoutTask;
            if (task is not null)
            {
                task.Conn = null;
                tracked.TimeoutTask = null;
            }
        }

        if (_udpTransport is not null)
        {
            try
            {
                s_pool.Return(_udpTransport);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }

            _udpTransport = null;
        }

        try { _attributes?.Return(); }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
        _attributes = null;

        GC.SuppressFinalize(this);
    }

    #endregion Dispose
}
