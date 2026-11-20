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
using Nalix.Environment.Memory;
using Nalix.Environment.Options;
using Nalix.Environment.Sequencing;
using Nalix.Environment.Time;
using Nalix.Network.Internal.Datagram;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Listeners.Datagram;

namespace Nalix.Network.Connections;

public sealed partial class DatagramConnection : IDatagramTransport
{
    private static readonly SequenceCounter s_detachedSequence = new();

    private DatagramListener _listener;
    private DatagramSocketConnection _socket;
    private DeliveryView?[] _views = [];

    /// <summary>Gets the session statistics of the protocol engine.</summary>
    public string EngineReport => _socket.Report;

    internal bool IsEstablished => _socket.IsEstablished;

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

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_listener), nameof(_socket))]
    private void InitializeTransport(DatagramListener listener, IDatagramProtection protection, DatagramEngineOptions options)
    {
        _listener = listener;
        _socket = new DatagramSocketConnection(this, listener, listener.TimingWheel, protection, options, this.DatagramConnectionId);
    }

    /// <summary>Processes a datagram routed to this session by the listener.</summary>
    /// <returns><see langword="true"/> when it authenticated.</returns>
    internal bool OnDatagram(ReadOnlySpan<byte> datagram, SocketAddress from)
    {
        if (!_socket.Receive(datagram, from, out IPEndPoint? firstPath))
        {
            return false;
        }

        if (firstPath is not null)
        {
            this.NetworkEndpoint = SocketEndpoint.FromEndPoint(firstPath);
        }

        this.LastPingTime = Clock.UnixMillisecondsNow();
        this.IncrementBytesReceived(datagram.Length);
        return true;
    }

    /// <summary>A message delivered by the engine.</summary>
    internal void OnEngineMessage(BufferLease message) => this.TriggerProcessEvent(message);

    private void SendCore(ReadOnlySpan<byte> message, DeliveryMethod method, byte channel)
    {
        if (!_socket.TrySend(message, method, channel, out bool closed))
        {
            throw new NetworkException(closed
                ? "The datagram session is closed."
                : $"The message ({message.Length} bytes) is too large for {method} or the send queue of channel {channel} is full.");
        }

        this.TriggerPostProcessEvent();
    }

    private void CloseEngine(string? reason) => _socket.Close(reason);

    private void DisposeTransport()
    {
        _socket.Dispose();
        _listener.OnConnectionClosed(this);
    }

    #endregion Engine

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
