// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Internal.Connections;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Listeners.Datagram;
using Nalix.Network.Options;

namespace Nalix.Network.Connections;

/// <summary>
/// A datagram session (Nalix Datagram Protocol over UDP) exposed as an <see cref="IConnection"/>, so handlers,
/// dispatch, permissions and the connection hub work unchanged. It is created by <see cref="DatagramListener"/>
/// when the first datagram of a ticket authenticates; identity, permission level and secret are copied from the
/// stream connection when the ticket was issued.
/// </summary>
/// <remarks>
/// <para>
/// The session has its own keys and lifetime: closing or rekeying the stream connection does not affect it, and
/// the identity and permission level copied at ticket time do not follow later changes on the stream connection.
/// To remove a user from both transports (a kick or a ban), also disconnect the datagram sessions whose
/// <see cref="StreamConnectionId"/> is the stream connection's <see cref="IConnection.ConnectionId"/>.
/// </para>
/// <para>Idle timeout is enforced by the protocol engine, not by the timing wheel.</para>
/// </remarks>
public sealed partial class DatagramConnection :
    IConnection,
    IConnectionErrorTracked,
    IConnectionTrafficMetrics,
    IPooledConnectContextPool
{
    #region Fields

    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;
    private static readonly ConnectionGuardOptions s_limitOptions = ConfigurationManager.Instance.Get<ConnectionGuardOptions>();
    private static readonly NetworkCallbackOptions s_callbackOptions = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();

    private ConnectionBacking? _backing;
    private volatile bool _disposed;

    #endregion Fields

    #region Constructor

    internal DatagramConnection(
        DatagramListener listener, ulong datagramConnectionId, IDatagramProtection protection,
        DatagramEngineOptions engineOptions, IOpCodeExtractor packetClassifier, in DatagramIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(packetClassifier);

        _backing = s_pool.Get<ConnectionBacking>();
        _backing.Initialize();

        this.PacketClassifier = packetClassifier;
        this.ConnectionId = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
        this.DatagramConnectionId = datagramConnectionId;
        this.UserId = identity.UserId;
        this.Level = identity.Level;
        this.Algorithm = identity.Algorithm;
        this.Secret = identity.Secret;
        this.StreamConnectionId = identity.StreamConnectionId;
        this.ExcludeFromIdleTimeout = true;
        this.NetworkEndpoint = Internal.Transport.SocketEndpoint.FromEndPoint(new IPEndPoint(IPAddress.Any, 0));

        this.InitializeTransport(listener, protection, engineOptions);
    }

    #endregion Constructor

    #region Properties

    /// <inheritdoc/>
    public bool IsDisposed => _disposed || Volatile.Read(ref _backing) is null;

    /// <inheritdoc/>
    public bool ExcludeFromIdleTimeout { get; set; }

    /// <inheritdoc/>
    public ulong ConnectionId { get; }

    /// <summary>
    /// Gets the identifier of the datagram session on the wire (from the ticket). It differs from
    /// <see cref="ConnectionId"/>, which identifies the connection in the hub.
    /// </summary>
    public ulong DatagramConnectionId { get; }

    /// <summary>
    /// Gets the <see cref="IConnection.ConnectionId"/> of the stream connection (TCP or WebSocket) that requested
    /// the ticket of this session. The session does not end with that connection.
    /// </summary>
    public ulong StreamConnectionId { get; }

    /// <inheritdoc/>
    public string? UserId { get; set; }

    /// <inheritdoc/>
    public IOpCodeExtractor PacketClassifier { get; }

    /// <inheritdoc/>
    public IConnection.ITransport Transport => Volatile.Read(ref _backing) is not null ? this : throw new ObjectDisposedException(nameof(DatagramConnection));

    /// <inheritdoc/>
    /// <remarks>The address of the first authenticated datagram.</remarks>
    public INetworkEndpoint NetworkEndpoint { get; private set; }

    /// <inheritdoc/>
    public IObjectMap<AttributeKey, object> Attributes
    {
        get
        {
            ConnectionBacking backing = Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(DatagramConnection));
            return ConnectionAttributeMaps.GetOrCreate(ref backing.Attributes);
        }
    }

    /// <summary>
    /// Gets the per-connection rate limit cache.
    /// </summary>
    public ConcurrentDictionary<ushort, object> RateLimitCache
    {
        get
        {
            ConnectionBacking backing = Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(DatagramConnection));
            return backing.RateLimitCache ??= new();
        }
    }

    /// <inheritdoc/>
    public int ErrorCount => Volatile.Read(ref _backing)?.ErrorCount ?? 0;

    /// <inheritdoc/>
    public long UpTime { get => (long)Clock.UnixTime().TotalMilliseconds - field; } = (long)Clock.UnixTime().TotalMilliseconds;

    /// <inheritdoc/>
    public long BytesSent => Volatile.Read(ref _backing)?.BytesSent ?? 0;

    /// <inheritdoc/>
    public long BytesReceived => Volatile.Read(ref _backing)?.BytesReceived ?? 0;

    /// <inheritdoc/>
    public long PacketsDropped => Volatile.Read(ref _backing)?.PacketsDropped ?? 0;

    /// <inheritdoc/>
    public int PendingPackets => Volatile.Read(ref _backing)?.PendingProcessCallbacks ?? 0;

    /// <inheritdoc/>
    public long LastPingTime
    {
        get => Interlocked.Read(ref field);
        private set => Interlocked.Exchange(ref field, value);
    } = Clock.UnixMillisecondsNow();

    /// <inheritdoc/>
    public PermissionLevel Level { get; set; }

    /// <inheritdoc/>
    public CipherSuiteType Algorithm { get; set; }

    /// <inheritdoc/>
    public Bytes32 Secret { get; set; }

    #endregion Properties

    #region Events

    /// <inheritdoc/>
    public event EventHandler<IConnectionEventArgs> ConnectionClosed
    {
        add => _ = Volatile.Read(ref _backing)?.ConnectionClosed += value;
        remove => _ = Volatile.Read(ref _backing)?.ConnectionClosed -= value;
    }

    /// <inheritdoc/>
    public event EventHandler<IConnectionEventArgs> MessageProcessed
    {
        add => _ = Volatile.Read(ref _backing)?.MessageProcessed += value;
        remove => _ = Volatile.Read(ref _backing)?.MessageProcessed -= value;
    }

    /// <inheritdoc/>
    public event EventHandler<IConnectionEventArgs> MessageProcessing
    {
        add => _ = Volatile.Read(ref _backing)?.MessageProcessing += value;
        remove => _ = Volatile.Read(ref _backing)?.MessageProcessing -= value;
    }

    #endregion Events

    #region Metrics

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

    #endregion Metrics

    #region Methods

    /// <inheritdoc/>
    public void Disconnect(string? reason = null)
    {
        if (_disposed)
        {
            return;
        }

        this.CloseEngine(reason);
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

        long windowTicks = (long)(s_limitOptions.ErrorWindow.TotalSeconds * Clock.TicksPerSecond);
        int count = backing.RecordError(Clock.MonoTicksNow(), windowTicks);
        if (s_limitOptions.MaxErrorThreshold > 0 && count >= s_limitOptions.MaxErrorThreshold)
        {
            this.Disconnect("Exceeded maximum error threshold.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>The idle timeout of a datagram session is negotiated in the ticket; this call has no effect.</remarks>
    public void UpdateIdleTimeout(int newTimeoutMs) => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newTimeoutMs);

    /// <inheritdoc/>
    public override string ToString()
        => $"DatagramConnection (Id={this.ConnectionId:X16}, Endpoint={this.NetworkEndpoint}, Disposed={_disposed})";

    #endregion Methods

    #region Dispose

    /// <inheritdoc/>
    public void Dispose()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null || Interlocked.CompareExchange(ref backing.DisposeState, 1, 0) != 0)
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
                    foreach (Delegate handler in backing.ConnectionClosed.GetInvocationList())
                    {
                        try
                        {
                            ((EventHandler<IConnectionEventArgs>)handler)(this, args);
                        }
                        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                        {
                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                            {
                                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.DatagramConnection:Dispose", "Close event error", ex));
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

                this.DisposeTransport();
                s_pool.Return(b);
            }
        }
    }

    #endregion Dispose

    #region Event Dispatch

    private void TriggerProcessEvent(BufferLease lease)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing == null)
        {
            lease.Dispose();
            return;
        }

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

            return;
        }

        ConnectionEventArgs args = this.AcquireEventArgs();
        args.Initialize(lease, this);

        if (!Internal.Transport.AsyncCallback.Invoke(MessageProcessingBridge, this, args, Internal.Transport.CallbackLane.Process, releasePendingPacketOnCompletion: true))
        {
            ((IPooledConnectContextPool)this).ReleasePendingPacket();
            _ = args.ExchangeLease(null);
            args.Dispose();
            lease.Dispose();
        }
    }

    private static void MessageProcessingBridge(object? sender, IConnectionEventArgs e)
    {
        if (sender is not DatagramConnection self)
        {
            e?.Dispose();
            return;
        }

        try
        {
            Volatile.Read(ref self._backing)?.MessageProcessing?.Invoke(self, e);
        }
        finally
        {
            e.Dispose();
        }
    }

    /// <summary>Raises <see cref="MessageProcessed"/> after a send, like the other connection types.</summary>
    private void TriggerPostProcessEvent()
    {
        EventHandler<IConnectionEventArgs>? processed = Volatile.Read(ref _backing)?.MessageProcessed;
        if (processed is null)
        {
            return;
        }

        ConnectionEventArgs args = this.AcquireEventArgs();
        args.Initialize(this);
        // A protocol whose post-processing only counts the message runs it here: a thread-pool hop per sent message
        // cost a work item and a thread wake-up for that.
        if (Internal.Transport.AsyncCallback.CanPostProcessInline(processed))
        {
            Internal.Transport.AsyncCallback.InvokeInline(MessageProcessedBridge, this, args);
            return;
        }

        if (!Internal.Transport.AsyncCallback.Invoke(MessageProcessedBridge, this, args, Internal.Transport.CallbackLane.Post))
        {
            args.Dispose();
        }
    }

    private static void MessageProcessedBridge(object? sender, IConnectionEventArgs e)
    {
        try
        {
            if (sender is DatagramConnection self)
            {
                Volatile.Read(ref self._backing)?.MessageProcessed?.Invoke(self, e);
            }
        }
        finally
        {
            e?.Dispose();
        }
    }

    #endregion Event Dispatch

    #region Pooling

    internal ConnectionEventArgs AcquireEventArgs()
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        ConnectionEventArgs? local = backing?.ArgsPool.Acquire(this, static (arg, self) => arg.Initialize(self));
        if (local != null)
        {
            return local;
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
        PooledConnectEventContext? local = backing?.ContextPool.Acquire(this, static (ctx, self) => ctx.LocalOwner = self);
        if (local != null)
        {
            local.LocalOwner = this;
            return local;
        }

        PooledConnectEventContext global = s_pool.Get<PooledConnectEventContext>();
        global.LocalOwner = this;
        return global;
    }

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

    #endregion Pooling
}

/// <summary>
/// What a datagram session inherits from the stream connection when its ticket is issued.
/// </summary>
internal readonly record struct DatagramIdentity(string? UserId, PermissionLevel Level, CipherSuiteType Algorithm, Bytes32 Secret, ulong StreamConnectionId);
