// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Listeners.Datagram;

namespace Nalix.Network.Internal.Datagram;

/// <summary>
/// Drives the protocol engine of one datagram session: serializes engine calls, sends what the engine produces and
/// keeps its deadline on the listener's <see cref="DatagramTimingWheel"/>. It is to <see cref="DatagramConnection"/>
/// what <see cref="SocketConnection"/> is to <see cref="Connection"/>.
/// </summary>
/// <remarks>
/// <para>
/// Datagrams are produced under the engine lock and sent after it is released, in batches of
/// <see cref="SendBatchSize"/>, so a slow socket never blocks the receive path. One thread drains at a time (the send
/// lock keeps the packet-number order on the wire); a thread that finds the drain busy leaves a request that the
/// draining thread picks up before it returns.
/// </para>
/// </remarks>
internal sealed partial class DatagramSocketConnection : IDatagramEngineHandler, DatagramTimingWheel.ITarget, IDisposable
{
    #region Constants

    private const int SendBatchSize = 8;

    #endregion Constants

    #region Fields

    private readonly DatagramConnection _owner;
    private readonly DatagramListener _listener;
    private readonly DatagramTimingWheel _wheel;
    private readonly DatagramEngine _engine;
    private readonly Lock _engineLock = new();

    /// <summary>The engine's active path last reported to the owner (guarded by the engine lock).</summary>
    private SocketAddress? _reportedPath;
    private readonly Lock _sendLock = new();

    private long _scheduledTick = long.MaxValue;
    private int _flushRequested;
    private int _closeScheduled;
    private bool _disposed;

    #endregion Fields

    public DatagramSocketConnection(
        DatagramConnection owner, DatagramListener listener, DatagramTimingWheel wheel,
        IDatagramProtection protection, DatagramEngineOptions options, ulong connectionId)
    {
        _owner = owner;
        _listener = listener;
        _wheel = wheel;
        _engine = new DatagramEngine(
            options, DatagramPeerLimits.From(options), protection, connectionId,
            isServer: true, this, DatagramEngine.NowMicros());
    }

    #region Properties

    /// <summary>Gets a value indicating whether the session completed its first authenticated exchange.</summary>
    public bool IsEstablished
    {
        get
        {
            lock (_engineLock)
            {
                return _engine.State == DatagramEngineState.Established;
            }
        }
    }

    /// <summary>Gets the engine report.</summary>
    public string Report
    {
        get
        {
            lock (_engineLock)
            {
                return _engine.GenerateReport();
            }
        }
    }

    /// <inheritdoc/>
    long DatagramTimingWheel.ITarget.ScheduledTick => Volatile.Read(ref _scheduledTick);

    #endregion Properties

    #region Methods

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_engineLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Any entry still on the wheel is now stale.
            Volatile.Write(ref _scheduledTick, long.MaxValue);
            _engine.Dispose();
        }
    }

    internal static IPEndPoint ToEndPoint(SocketAddress address)
    {
        IPAddress any = address.Family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        IPEndPoint endPoint = (IPEndPoint)new IPEndPoint(any, 0).Create(address);
        return endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;
    }

    #endregion Methods

    #region Receive

    /// <summary>
    /// Processes a datagram routed to this session.
    /// </summary>
    /// <param name="datagram">The UDP payload.</param>
    /// <param name="from">The sender address (the listener's buffer; the engine copies what it keeps).</param>
    /// <param name="newPath">
    /// The session's path when this datagram changed it: the first path when the datagram established the session,
    /// or the new address once a migration (NAT rebinding, a handover) was validated; otherwise <see langword="null"/>.
    /// </param>
    /// <returns><see langword="true"/> when the datagram authenticated.</returns>
    public bool Receive(ReadOnlySpan<byte> datagram, SocketAddress from, out IPEndPoint? newPath)
    {
        bool accepted;
        newPath = null;

        lock (_engineLock)
        {
            if (_disposed)
            {
                return false;
            }

            accepted = _engine.Receive(datagram, from, DatagramEngine.NowMicros());

            // The engine replaces its active path object when a migration is validated, and never mutates it.
            if (accepted && _engine.ActivePath is { } path && !ReferenceEquals(path, _reportedPath))
            {
                _reportedPath = path;
                newPath = ToEndPoint(path);
            }
        }

        this.Flush();
        return accepted;
    }

    #endregion Receive

    #region IDatagramEngineHandler

    /// <inheritdoc/>
    void IDatagramEngineHandler.OnMessage(BufferLease message, DeliveryMethod method, byte channel) => _owner.OnEngineMessage(message);

    /// <inheritdoc/>
    /// <remarks>Raised under the engine lock: the owner is disposed from the thread pool.</remarks>
    void IDatagramEngineHandler.OnClosed(DatagramCloseCode code, ulong applicationCode, string reason, bool remote)
    {
        if (Interlocked.Exchange(ref _closeScheduled, 1) == 0)
        {
            _ = ThreadPool.UnsafeQueueUserWorkItem(static owner => owner.Dispose(), _owner, preferLocal: false);
        }
    }

    #endregion IDatagramEngineHandler

    #region Timer

    /// <summary>The wheel reached this session's deadline (runs on the thread pool).</summary>
    void IThreadPoolWorkItem.Execute()
    {
        lock (_engineLock)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _scheduledTick, long.MaxValue);
            _engine.OnTimer(DatagramEngine.NowMicros());
        }

        this.Flush();
    }

    /// <summary>Moves the deadline earlier when the engine needs it. Called under the engine lock.</summary>
    private void ArmLocked()
    {
        long next = _engine.NextTimer;
        if (next == long.MaxValue)
        {
            return;
        }

        long tick = _wheel.ToTick(next);
        if (tick < Volatile.Read(ref _scheduledTick))
        {
            Volatile.Write(ref _scheduledTick, tick);
            _wheel.Schedule(this, tick);
        }
    }

    #endregion Timer
}
