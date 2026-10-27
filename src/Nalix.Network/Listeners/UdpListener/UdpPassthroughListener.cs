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
using Nalix.Abstractions.Networking;
using Nalix.Environment.Memory;
using Nalix.Environment.Time;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.RateLimiting;

#pragma warning disable IDE0130 // Namespace does not match folder structure

namespace Nalix.Network.Listeners.Udp;

/// <summary>
/// Provides a UDP listener that bypasses Nalix frame decoding, session-token
/// resolution, and security-layer processing. Received datagrams are forwarded
/// directly to the configured protocol as raw bytes.
/// </summary>
/// <remarks>
/// <para>
/// This listener is intended for protocols that implement their own framing and
/// session management over UDP, such as Minecraft Bedrock (RakNet).
/// </para>
/// <para>
/// Each unique remote UDP endpoint is tracked as a lightweight
/// <see cref="PassthroughConnection"/>. These connections are registered
/// into the shared <see cref="TimingWheel"/> for idle-timeout management,
/// eliminating the need for a dedicated per-listener Timer.
/// </para>
/// <para>
/// Idle connections are automatically evicted by the <see cref="TimingWheel"/>
/// when <see cref="IConnection.LastPingTime"/> exceeds the configured threshold.
/// Per-connection backpressure prevents a single endpoint from monopolizing
/// the ThreadPool.
/// </para>
/// <para>
/// <b>Safety note:</b> Because Nalix session tokens, replay protection, and
/// authentication hooks are all bypassed, the protocol layer is fully responsible
/// for its own security model. An outer <see cref="ConnectionGuard"/> provides
/// IP-level rate limiting to mitigate DDoS.
/// </para>
/// </remarks>
public sealed class UdpPassthroughListener : UdpListenerBase
{
    #region Fields

    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    private readonly ConcurrentDictionary<EndPoint, PassthroughConnection> _connections = new();

#pragma warning disable CA2213 // Singleton services — owned by InstanceManager, not by this listener.
    private readonly TimingWheel? _timing;
    private readonly IConnectionGuard? _connGuard;
#pragma warning restore CA2213

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="UdpPassthroughListener"/> class.
    /// </summary>
    public UdpPassthroughListener(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard) : base(protocol, hub)
    {
        _timing = InstanceManager.Instance.GetExistingInstance<TimingWheel>();
        _connGuard = guard;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UdpPassthroughListener"/> class.
    /// </summary>
    public UdpPassthroughListener(ushort port, IProtocol protocol, IConnectionHub hub, IConnectionGuard guard) : base(port, protocol, hub)
    {
        _timing = InstanceManager.Instance.GetExistingInstance<TimingWheel>();
        _connGuard = guard;
    }

    #endregion Constructors

    #region Lifecycle

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (PassthroughConnection connection in _connections.Values)
            {
                connection.Dispose();
            }
            _connections.Clear();
        }

        base.Dispose(disposing);
    }

    #endregion Lifecycle

    #region ProcessDatagram

    /// <summary>
    /// Bypasses all Nalix datagram processing and forwards the raw payload
    /// directly to the configured protocol. Virtual connections are registered
    /// into the shared <see cref="TimingWheel"/> for automatic idle cleanup.
    /// </summary>
    protected override void ProcessDatagram(BufferLease lease, EndPoint? remoteEndPoint)
    {
        if (lease is null || remoteEndPoint is null)
        {
            lease?.Dispose();
            return;
        }

        if (remoteEndPoint is not IPEndPoint ipEndPoint)
        {
            lease.Dispose();
            return;
        }

        if (ipEndPoint.Address.IsIPv4MappedToIPv6)
        {
            ipEndPoint = new IPEndPoint(ipEndPoint.Address.MapToIPv4(), ipEndPoint.Port);
        }

        if (!_connections.TryGetValue(ipEndPoint, out PassthroughConnection? connection) || connection.IsDisposed)
        {
            connection = this.GetOrCreateConnection(ipEndPoint, lease);
            if (connection is null)
            {
                return;
            }
        }

        connection.LastPingTime = Clock.UnixMillisecondsNow();
        connection.BindUdp(this.ListenerSocket!, ipEndPoint);

        if (!connection.TryAcquirePendingPacket())
        {
            lease.Dispose();
            return;
        }

        lease.IsReliable = false;

        PassthroughArgs args = s_pool.Get<PassthroughArgs>();
        args.Initialize(lease, connection, this);

        if (!ThreadPool.UnsafeQueueUserWorkItem(s_processCallback, args))
        {
            connection.ReleasePendingPacket();
            args.Dispose();
        }
    }

    #endregion ProcessDatagram

    #region Connection Close Handler

    private void OnConnectionClosed(object? sender, IConnectionEventArgs args)
    {
        if (args?.Connection is not PassthroughConnection connection)
        {
            return;
        }

        connection.ConnectionClosed -= this.OnConnectionClosed;
        _ = _connections.TryRemove(connection.EndPointKey, out _);
        _connGuard?.OnConnectionClosed(sender, args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PassthroughConnection? GetOrCreateConnection(IPEndPoint ipEndPoint, BufferLease lease)
    {
        if (_connGuard is not null && !(_connGuard is ConnectionGuard guard ? guard.TryAcceptPassthrough(ipEndPoint) : _connGuard.TryAccept(ipEndPoint)))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpPassthroughListener:OnConnectionClosed", $"rate-limit-drop ip-end-point={ipEndPoint}"));
            }

            lease.Dispose();
            return null;
        }

        PassthroughConnection newConnection = new(this.Protocol.OpCodeExtractor, ipEndPoint, _timing);

        PassthroughConnection connection = _connections.AddOrUpdate(
            ipEndPoint,
            static (_, arg) => arg,
            static (_, existing, arg) => existing.IsDisposed ? arg : existing,
            newConnection
        );

        if (connection != newConnection)
        {
            PassthroughArgs dummyArgs = s_pool.Get<PassthroughArgs>();

            dummyArgs.Initialize(null!, newConnection, this);
            _connGuard?.OnConnectionClosed(this, dummyArgs);
            dummyArgs.Dispose();
            newConnection.Dispose();

            if (connection.IsDisposed)
            {
                lease.Dispose();
                return null;
            }
        }
        else
        {
            connection.ConnectionClosed += this.OnConnectionClosed;
            _timing?.Register(connection);
        }

        return connection;
    }


    #endregion Connection Close Handler

    #region Async Callback

    private static readonly WaitCallback s_processCallback = static state =>
    {
        if (state is not PassthroughArgs args)
        {
            return;
        }

        PassthroughConnection? connection = args.Connection as PassthroughConnection;

        try
        {
            if (connection is null || connection.IsDisposed)
            {
                return;
            }

            args.Listener?.Protocol.FrameProcessor.ProcessFrame(args.Listener, args);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            HANDLE_PASSTHROUGH_ERROR(connection, ex);
        }
        finally
        {
            connection?.ReleasePendingPacket();
            args.Dispose();
        }
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HANDLE_PASSTHROUGH_ERROR(PassthroughConnection? connection, Exception __) => connection?.IncrementErrorCount();

    #endregion Async Callback

    #region ProcessFrame

    /// <inheritdoc />
    public override bool IsAuthenticated(IConnection connection, EndPoint remoteEndPoint, ReadOnlySpan<byte> payload) => true;

    #endregion ProcessFrame

    #region PassthroughArgs

    private sealed class PassthroughArgs : IConnectionEventArgs, IPoolable
    {
        private IBufferLease? _lease;
        private IConnection? _connection;
        private UdpPassthroughListener? _listener;

        public PassthroughArgs()
        {
        }

        public void Initialize(IBufferLease lease, IConnection connection, UdpPassthroughListener listener)
        {
            _lease = lease;
            _connection = connection;
            _listener = listener;
        }

        public IConnection Connection => _connection ?? throw new InvalidOperationException("Args not initialized.");

        public IBufferLease? Lease => _lease;

        public INetworkEndpoint? NetworkEndpoint => this.Connection.NetworkEndpoint;

        internal UdpPassthroughListener? Listener => _listener;

        public void ResetForPool()
        {
            _lease = null;
            _connection = null;
            _listener = null;
        }

        public void Dispose()
        {
            IBufferLease? lease = _lease;
            _lease = null;
            lease?.Dispose();

            s_pool.Return(this);
        }
    }

    #endregion PassthroughArgs
}
