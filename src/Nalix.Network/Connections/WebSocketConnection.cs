// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
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
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

namespace Nalix.Network.Connections;

/// <summary>
/// Represents a network connection that manages WebSocket communication, stream
/// transformation, and event handling.
/// </summary>
public sealed class WebSocketConnection :
    IConnection,
    IConnectionErrorTracked,
    IConnectionTrafficMetrics,
    IPooledConnectContextPool,
    TimingWheel.ITimeoutTrackedConnection
{
    #region Fields

    private static readonly ObjectPoolManager s_pool;
    private static readonly TimingWheel s_timingWheel;
    private static readonly TimingWheelOptions s_timingWheelOptions;
    private static readonly ConnectionGuardOptions s_limitOptions;
    private static readonly NetworkCallbackOptions s_callbackOptions;

    private ConnectionBacking? _backing;
    private volatile bool _disposed;

    #endregion Fields

    #region Constructor

    static WebSocketConnection()
    {
        s_pool = ObjectPoolManager.Shared;
        s_timingWheel = InstanceManager.Instance.GetOrCreateInstance<TimingWheel>();

        s_limitOptions = ConfigurationManager.Instance.Get<ConnectionGuardOptions>();
        s_timingWheelOptions = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        s_callbackOptions = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();

        NetworkPoolInitializer.InitializeWebSocket();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketConnection"/> class.
    /// </summary>
    /// <param name="webSocket">The underlying WebSocket instance.</param>
    /// <param name="remoteEndPoint">The remote endpoint of the client.</param>
    /// <param name="packetClassifier">The opcode extractor for classifying incoming packets.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="webSocket"/> is null.</exception>
    public WebSocketConnection(WebSocket webSocket, IOpCodeExtractor packetClassifier, EndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(packetClassifier);

        _disposed = false;

        _backing = s_pool.Get<ConnectionBacking>();
        _backing.Initialize();

        this.Secret = Bytes32.Zero;
        this.PacketClassifier = packetClassifier;
        this.IdleTimeoutMs = s_timingWheelOptions.IdleTimeoutMs;
        this.ConnectionId = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
        this.NetworkEndpoint = SocketEndpoint.FromEndPoint(remoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0));

        // Transport owns WebSocket — ownership transfer
        _backing.WsTransport = s_pool.Get<WebSocketTransport>();
        _backing.WsTransport.Initialize(this, webSocket);

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.WebSocketConnection:UnknownMethod", $"created remote-endpoint={this.NetworkEndpoint} connection-id={this.ConnectionId:X16}"));
        }
    }

    #endregion Constructor

    #region Properties

    /// <inheritdoc/>
    public bool IsDisposed => _disposed || Volatile.Read(ref _backing) is null;

    /// <inheritdoc/>
    public bool IsUdpCreated => false; // UDP is not supported over WebSocket

    /// <inheritdoc/>
    public bool ExcludeFromIdleTimeout { get; set; }

    /// <inheritdoc/>
    public ulong ConnectionId { get; }

    /// <inheritdoc/>
    public string? UserId { get; set; }

    /// <inheritdoc/>
    public IOpCodeExtractor PacketClassifier { get; }

    /// <inheritdoc/>
    public IConnection.ITransport TCP => Volatile.Read(ref _backing)?.WsTransport ?? throw new ObjectDisposedException(nameof(WebSocketConnection));

    /// <inheritdoc/>
    public IConnection.ITransport? UDP => null;

    /// <inheritdoc/>
    public INetworkEndpoint NetworkEndpoint { get; }

    /// <inheritdoc />
    public IObjectMap<AttributeKey, object> Attributes
    {
        get
        {
            ConnectionBacking backing = Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(WebSocketConnection));
            return backing.Attributes ??= ObjectMap<AttributeKey, object>.Rent();
        }
    }

    /// <inheritdoc />
    public ConcurrentDictionary<ushort, object> RateLimitCache
    {
        get
        {
            ConnectionBacking backing = Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(WebSocketConnection));
            return backing.RateLimitCache ??= new();
        }
    }

    /// <inheritdoc/>
    public int ErrorCount => Volatile.Read(ref _backing)?.ErrorCount ?? 0;

    /// <summary>
    /// Gets the connection uptime in milliseconds (how long the connection has been active).
    /// </summary>
    public long UpTime { get => (long)Clock.UnixTime().TotalMilliseconds - field; } = (long)Clock.UnixTime().TotalMilliseconds;

    /// <inheritdoc/>
    public long BytesSent => Volatile.Read(ref _backing)?.BytesSent ?? 0;

    /// <inheritdoc/>
    public long BytesReceived => Volatile.Read(ref _backing)?.BytesReceived ?? 0;

    /// <inheritdoc/>
    public long PacketsDropped => Volatile.Read(ref _backing)?.PacketsDropped ?? 0;

    /// <summary>
    /// Returns the number of packets currently pending in the async callback pipeline.
    /// </summary>
    public int PendingPackets => Volatile.Read(ref _backing)?.PendingProcessCallbacks ?? 0;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementBytesSent(int bytes)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Add(ref backing.BytesSent, bytes);
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementBytesReceived(int bytes)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Add(ref backing.BytesReceived, bytes);
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementPacketsDropped()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Increment(ref backing.PacketsDropped);
        }
    }

    /// <summary>
    /// Gets or sets the timestamp (in milliseconds) of the last received ping.
    /// </summary>
    public long LastPingTime
    {
        get => Interlocked.Read(ref field);
        set => Interlocked.Exchange(ref field, value);
    } = Clock.UnixMillisecondsNow();

    /// <inheritdoc/>
    public PermissionLevel Level { get; set; } = PermissionLevel.NONE;

    /// <inheritdoc/>
    public CipherSuiteType Algorithm { get; set; } = CipherSuiteType.Chacha20Poly1305;

    /// <inheritdoc/>
    public Bytes32 Secret { get; set; }

    /// <inheritdoc />
    public int IdleTimeoutMs { get; set; }

    /// <inheritdoc/>
    public int TimeoutVersion { get; set; }

    /// <inheritdoc/>
    public bool IsRegisteredInWheel { get; set; }

    /// <summary>
    /// Tracks the current timeout task in the TimingWheel.
    /// Used for manual reference breaking during Dispose to allow instant GC.
    /// </summary>
    TimingWheel.TimeoutTask? TimingWheel.ITimeoutTrackedConnection.TimeoutTask { get; set; }

    #endregion Properties

    #region Events

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    #region Internal Helpers

    internal void AddBytesSent(long count)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Add(ref backing.BytesSent, count);
        }
    }

    internal void AddBytesReceived(long count)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing != null)
        {
            _ = Interlocked.Add(ref backing.BytesReceived, count);
        }
    }
    internal void UpdateLastPingTime() => this.LastPingTime = Clock.UnixMillisecondsNow();

    internal void TriggerPostProcessEvent()
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

    internal void TriggerProcessEvent(BufferLease lease)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null)
        {
            lease.Dispose();
            return;
        }

        // Fast path: see Connection.OnFrameReceived — a non-blocking frame processor runs on the
        // receive loop directly instead of via a thread-pool work item.
        if (Internal.Transport.AsyncCallback.CanRunInline(backing.MessageProcessing))
        {
            ConnectionEventArgs inlineArgs = this.AcquireEventArgs();
            inlineArgs.Initialize(lease, this);
            Internal.Transport.AsyncCallback.InvokeInline(MessageProcessingBridge, this, inlineArgs);
            return;
        }

        int pending = Interlocked.Increment(ref backing.PendingProcessCallbacks);

        if (pending > s_callbackOptions.MaxPerConnectionPendingPackets)
        {
            _ = Interlocked.Decrement(ref backing.PendingProcessCallbacks);
            lease.Dispose();
            this.IncrementPacketsDropped();

            if (s_callbackOptions.OverflowPolicy == NetworkOverflowPolicy.Disconnect)
            {
                this.Disconnect("Exceeded MaxPerConnectionPendingPackets threshold.");
            }

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog("NW.WebSocketConnection:TriggerProcessEvent", $"receive throttle triggered remote-endpoint={this.NetworkEndpoint}"));
            }

            return;
        }

        ConnectionEventArgs args = this.AcquireEventArgs();
        args.Initialize(lease, this);

        if (!Internal.Transport.AsyncCallback.Invoke(MessageProcessingBridge, this, args, CallbackLane.Process, releasePendingPacketOnCompletion: true))
        {
            ((IPooledConnectContextPool)this).ReleasePendingPacket();
            _ = args.ExchangeLease(null);
            args.Dispose();
            lease.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void MessageProcessingBridge(object? sender, IConnectionEventArgs e)
    {
        if (e is null)
        {
            return;
        }

        if (sender is not WebSocketConnection self)
        {
            e.Dispose();
            return;
        }

        SAFE_PROCESS_EVENT_BRIDGE(self, e);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SAFE_PROCESS_EVENT_BRIDGE(WebSocketConnection self, IConnectionEventArgs e)
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

        if (sender is not WebSocketConnection self)
        {
            e.Dispose();
            return;
        }

        SAFE_POST_PROCESS_EVENT_BRIDGE(self, e);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SAFE_POST_PROCESS_EVENT_BRIDGE(WebSocketConnection self, IConnectionEventArgs e)
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

    #endregion Internal Helpers

    #region Methods

    /// <inheritdoc/>
    public void Disconnect(string? reason = null)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.WebSocketConnection:Disconnect", reason ?? "Unknown"));
        }

        if (_disposed)
        {
            return;
        }

        // Do NOT set CloseSignaled here — Dispose() uses that same flag to decide
        // whether to fire ConnectionClosed. Setting it first would make Dispose()
        // silently skip the event, so ConnectionHub never unregisters this connection.
        // Dispose() is already idempotent via DisposeState, so just delegate to it.
        this.Dispose();
    }

    /// <inheritdoc/>
    public void IncrementErrorCount()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null)
        {
            return;
        }

        int count = Interlocked.Increment(ref backing.ErrorCount);

        if (s_limitOptions.MaxErrorThreshold > 0 && count >= s_limitOptions.MaxErrorThreshold)
        {
            this.Disconnect("Exceeded maximum error threshold.");
        }
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

    /// <inheritdoc/>
    public void Dispose()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref backing.DisposeState, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref backing.CloseSignaled, 1) == 0 && backing.ConnectionClosed != null)
            {
                ConnectionEventArgs args = this.AcquireEventArgs();
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
                        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                        {
                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                            {
                                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.WebSocketConnection:Dispose", "Close event error", ex));
                            }
                        }
                    }
                }
                finally
                {
                    args.Dispose();
                }
            }

            // Break timing wheel reference AFTER close handlers have run
            // so TimingWheel.Unregister can retrieve and remove the TimeoutTask from the bucket.
            TimingWheel.TimeoutTask? task = ((TimingWheel.ITimeoutTrackedConnection)this).TimeoutTask;
            if (task is not null)
            {
                task.Conn = null;
                ((TimingWheel.ITimeoutTrackedConnection)this).TimeoutTask = null;
            }
        }
        finally
        {
            ConnectionBacking? b = Interlocked.Exchange(ref _backing, null);
            if (b != null)
            {
                Volatile.Write(ref b.DisposeState, 2);
                _disposed = true;

                this.Secret = Bytes32.Zero;

                try { b.Attributes?.Return(); b.Attributes = null; }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }

                // Transport.Dispose() handles WebSocket Abort + Dispose
                try
                {
                    if (b.WsTransport != null)
                    {
                        b.WsTransport.Dispose();
                        s_pool.Return(b.WsTransport);
                        b.WsTransport = null;
                    }
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }

                // Return backing to pool
                s_pool.Return(b);
            }
        }

        GC.SuppressFinalize(this);
    }

    #endregion Dispose Pattern

    #region Internal Pooling

    internal ConnectionEventArgs AcquireEventArgs()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        ConnectionEventArgs? argLocal = backing?.ArgsPool.Acquire(this, static (arg, self) => arg.Initialize(self));
        if (argLocal != null)
        {
            return argLocal;
        }

        ConnectionEventArgs args = s_pool.Get<ConnectionEventArgs>();
        args.Initialize(this);

        return args;
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

    PooledConnectEventContext IPooledConnectContextPool.AcquireContext()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        PooledConnectEventContext? ctxLocal = backing?.ContextPool.Acquire(this, static (ctx, self) => ctx.LocalOwner = self);
        if (ctxLocal != null)
        {
            ctxLocal.LocalOwner = this;
            return ctxLocal;
        }

        PooledConnectEventContext ctxGlobal = s_pool.Get<PooledConnectEventContext>();
        ctxGlobal.LocalOwner = this;

        return ctxGlobal;
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
}
