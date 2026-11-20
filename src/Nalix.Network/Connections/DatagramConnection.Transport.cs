// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Security;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.Environment.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Listeners.Datagram;

namespace Nalix.Network.Connections;

public sealed partial class DatagramConnection : IDatagramTransport
{
    private static readonly SequenceCounter s_detachedSequence = new();

    private readonly Lock _engineLock = new();
    private readonly byte[] _sendBuffer = new byte[DatagramEngine.MaxDatagramSize];
    private DatagramListener _listener;
    private DatagramEngine _engine;
    private Timer? _timer;
    private long _timerDue = long.MaxValue;
    private DeliveryView?[] _views = [];
    private int _closeScheduled;

    /// <summary>Gets the session statistics of the protocol engine.</summary>
    public string EngineReport
    {
        get
        {
            lock (_engineLock)
            {
                return _engine.GenerateReport();
            }
        }
    }

    internal bool IsEstablished
    {
        get
        {
            lock (_engineLock)
            {
                return _engine.State == DatagramEngineState.Established;
            }
        }
    }

    private SequenceCounter SendSequenceCounter => Volatile.Read(ref _backing)?.SendSequence ?? s_detachedSequence;

    private SequenceCounter ReceiveSequenceCounter => Volatile.Read(ref _backing)?.ReceiveSequence ?? s_detachedSequence;

    #region ITransport

    /// <inheritdoc/>
    TransportFraming IConnection.ITransport.Framing => TransportFraming.UInt16LengthPrefixed;

    /// <inheritdoc/>
    ISequenceCounter ITransportSequencer.SendSequence => this.SendSequenceCounter;

    /// <inheritdoc/>
    ISequenceCounter ITransportSequencer.ReceiveSequence => this.ReceiveSequenceCounter;

    /// <inheritdoc/>
    uint ITransportSequencer.NextSendSequence() => this.SendSequenceCounter.Next();

    /// <inheritdoc/>
    uint ITransportSequencer.NextReceiveSequence() => this.ReceiveSequenceCounter.Next();

    /// <inheritdoc/>
    uint ITransportSequencer.CurrentSendSequence => this.SendSequenceCounter.Current();

    /// <inheritdoc/>
    uint ITransportSequencer.CurrentReceiveSequence => this.ReceiveSequenceCounter.Current();

    /// <inheritdoc/>
    void IConnection.ITransport.Send(ReadOnlySpan<byte> message) => this.SendCore(message, DeliveryMethod.ReliableOrdered, 0);

    /// <inheritdoc/>
    ValueTask IConnection.ITransport.SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        => this.SendAsync(message, DeliveryMethod.ReliableOrdered, 0, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>Receiving is driven by the datagram listener; nothing to start.</remarks>
    void IConnection.ITransport.BeginReceive(CancellationToken cancellationToken)
    {
    }

    /// <inheritdoc/>
    /// <remarks>Datagram messages are whole Nalix frames; the framing mode does not apply.</remarks>
    void IConnection.ITransport.UseFraming(TransportFraming framing)
    {
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, DeliveryMethod method, byte channel, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        try
        {
            this.SendCore(message.Span, method, channel);
            return ValueTask.CompletedTask;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            return ValueTask.FromException(ex);
        }
    }

    /// <inheritdoc/>
    public IConnection.ITransport WithDelivery(DeliveryMethod method, byte channel)
    {
        if (method == DeliveryMethod.ReliableOrdered && channel == 0)
        {
            return this;
        }

        int index = ((int)method * DatagramEngineOptions.MaxChannels) + channel;
        DeliveryView?[] views = Volatile.Read(ref _views);
        if (views.Length == 0)
        {
            _ = Interlocked.CompareExchange(ref _views, new DeliveryView?[5 * DatagramEngineOptions.MaxChannels], views);
            views = Volatile.Read(ref _views);
        }

        return views[index] ??= new DeliveryView(this, method, channel);
    }

    #endregion ITransport

    #region Engine

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_listener), nameof(_engine))]
    private void InitializeTransport(DatagramListener listener, IDatagramProtection protection, DatagramEngineOptions options)
    {
        _listener = listener;
        _engine = new DatagramEngine(
            options, DatagramPeerLimits.From(options), protection, this.DatagramConnectionId,
            isServer: true, new EngineHandler(this), DatagramEngine.NowMicros());
        _timer = new Timer(static state => ((DatagramConnection)state!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Processes a datagram routed to this session by the listener.</summary>
    /// <returns><see langword="true"/> when it authenticated.</returns>
    internal bool OnDatagram(ReadOnlySpan<byte> datagram, SocketAddress from)
    {
        bool accepted;
        lock (_engineLock)
        {
            if (_disposed)
            {
                return false;
            }

            long now = DatagramEngine.NowMicros();
            bool firstDatagram = _engine.State == DatagramEngineState.Pending;
            accepted = _engine.Receive(datagram, from, now);

            if (accepted && firstDatagram && _engine.ActivePath is { } path)
            {
                this.NetworkEndpoint = SocketEndpoint.FromEndPoint(ToEndPoint(path));
            }

            this.FlushLocked(now);
        }

        if (accepted)
        {
            this.LastPingTime = Clock.UnixMillisecondsNow();
            this.IncrementBytesReceived(datagram.Length);
        }

        return accepted;
    }

    private void SendCore(ReadOnlySpan<byte> message, DeliveryMethod method, byte channel)
    {
        lock (_engineLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            long now = DatagramEngine.NowMicros();
            if (!_engine.TrySend(message, method, channel, 0, now))
            {
                throw new NetworkException(_engine.State >= DatagramEngineState.Draining
                    ? "The datagram session is closed."
                    : $"The message ({message.Length} bytes) is too large for {method} or the send queue of channel {channel} is full.");
            }

            this.FlushLocked(now);
        }

        this.TriggerPostProcessEvent();
    }

    private void OnTimer()
    {
        lock (_engineLock)
        {
            if (_disposed)
            {
                return;
            }

            _timerDue = long.MaxValue;
            long now = DatagramEngine.NowMicros();
            _engine.OnTimer(now);
            this.FlushLocked(now);
        }
    }

    /// <summary>Sends everything the engine produces and re-arms the timer. Called under the engine lock.</summary>
    private void FlushLocked(long now)
    {
        int length;
        while ((length = _engine.Poll(_sendBuffer, now, out SocketAddress? to)) > 0)
        {
            SocketAddress? target = to ?? _engine.ActivePath;
            if (target is not null)
            {
                _listener.SendTo(_sendBuffer.AsSpan(0, length), target);
                this.IncrementBytesSent(length);
            }
        }

        long next = _engine.NextTimer;
        if (next == long.MaxValue)
        {
            return;
        }

        if (next < _timerDue)
        {
            _timerDue = next;
            long delayMs = Math.Max(0, (next - now + 999) / 1000);
            _ = _timer?.Change(delayMs, Timeout.Infinite);
        }
    }

    private void CloseEngine(string? reason)
    {
        lock (_engineLock)
        {
            if (_disposed)
            {
                return;
            }

            long now = DatagramEngine.NowMicros();
            _engine.Close(DatagramCloseCode.NoError, reason, now);
            this.FlushLocked(now);
        }
    }

    private void DisposeTransport()
    {
        lock (_engineLock)
        {
            _timer?.Dispose();
            _timer = null;
            _engine.Dispose();
        }

        _listener.OnConnectionClosed(this);
    }

    /// <summary>The engine closed (by the peer or idle timeout): dispose outside the engine lock.</summary>
    private void OnEngineClosed()
    {
        if (Interlocked.Exchange(ref _closeScheduled, 1) == 0)
        {
            _ = ThreadPool.UnsafeQueueUserWorkItem(static self => self.Dispose(), this, preferLocal: false);
        }
    }

    private static IPEndPoint ToEndPoint(SocketAddress address)
    {
        IPAddress any = address.Family == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        IPEndPoint endPoint = (IPEndPoint)new IPEndPoint(any, 0).Create(address);
        return endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;
    }

    #endregion Engine

    /// <summary>Receives the engine's output.</summary>
    private sealed class EngineHandler(DatagramConnection owner) : IDatagramEngineHandler
    {
        public void OnMessage(BufferLease message, DeliveryMethod method, byte channel)
        {
            // Every datagram message passed the session AEAD and replay window.
            message.IsReliable = true;
            owner.TriggerProcessEvent(message);
        }

        public void OnClosed(DatagramCloseCode code, ulong applicationCode, string reason, bool remote) => owner.OnEngineClosed();
    }

    /// <summary>A view of the transport that sends with a fixed delivery method and channel.</summary>
    private sealed class DeliveryView(DatagramConnection owner, DeliveryMethod method, byte channel) : IConnection.ITransport
    {
        public TransportFraming Framing => TransportFraming.UInt16LengthPrefixed;

        public ISequenceCounter SendSequence => owner.SendSequenceCounter;

        public ISequenceCounter ReceiveSequence => owner.ReceiveSequenceCounter;

        public uint CurrentSendSequence => owner.SendSequenceCounter.Current();

        public uint CurrentReceiveSequence => owner.ReceiveSequenceCounter.Current();

        public uint NextSendSequence() => owner.SendSequenceCounter.Next();

        public uint NextReceiveSequence() => owner.ReceiveSequenceCounter.Next();

        public void Send(ReadOnlySpan<byte> message) => owner.SendCore(message, method, channel);

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
            => owner.SendAsync(message, method, channel, cancellationToken);

        public void BeginReceive(CancellationToken cancellationToken = default)
        {
        }

        public void UseFraming(TransportFraming framing)
        {
        }
    }
}
