// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security.Datagram;
using Nalix.Codec.Transforms;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;
using Nalix.SDK.Options;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Transport;

/// <summary>
/// Client side of a Nalix Datagram Protocol session (NDP over UDP). It obtains a ticket over an established
/// stream session (TCP or WebSocket), derives its own keys from the stream session secret and then runs on UDP
/// alone: closing the stream afterwards does not affect it.
/// </summary>
/// <remarks>
/// Every datagram is sealed with ChaCha20-Poly1305 by the session, so frames are not encrypted a second time;
/// the <c>encrypt</c> arguments of the <see cref="TransportSession"/> send methods are ignored.
/// </remarks>
[UnsupportedOSPlatform("browser")]
public sealed class DatagramSession : TransportSession
{
    private readonly TransportSession _stream;
    private readonly Lock _engineLock = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly byte[] _sendBuffer = new byte[DatagramEngine.MaxDatagramSize];
    private readonly List<(BufferLease Message, DeliveryMethod Method, byte Channel)> _deliveries = [];

    private Socket? _socket;
    private DatagramEngine? _engine;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Timer? _timer;
    private long _timerDue = long.MaxValue;
    private TaskCompletionSource? _established;
    private Exception? _closeError;
    private int _disposed;

    /// <summary>
    /// Initializes a datagram session bound to an established stream session.
    /// </summary>
    /// <param name="stream">The stream session that requests the ticket; its handshake must be complete.</param>
    public DatagramSession(TransportSession stream) : base(stream?.State) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <inheritdoc/>
    public override TransportOptions Options => _stream.Options;

    /// <inheritdoc/>
    public override bool IsConnected
    {
        get
        {
            lock (_engineLock)
            {
                return _engine is { State: DatagramEngineState.Established };
            }
        }
    }

    /// <summary>Gets the ticket of the current session, once connected.</summary>
    public DatagramTicketInfo? Ticket { get; private set; }

    /// <summary>Gets the session statistics, or <see langword="null"/> when not connected.</summary>
    public string? EngineReport
    {
        get
        {
            lock (_engineLock)
            {
                return _engine?.GenerateReport();
            }
        }
    }

    internal override bool HasDirectPendingDispatch => true;

    /// <inheritdoc/>
    public override event EventHandler? OnConnected;

    /// <inheritdoc/>
    public override event EventHandler<Exception>? OnDisconnected;

    /// <inheritdoc/>
    public override event EventHandler<IBufferLease>? OnMessageReceived;

    /// <inheritdoc/>
    public override event EventHandler<Exception>? OnError;

    /// <summary>
    /// Requests a ticket over the stream session, opens the UDP socket and waits until the server authenticated
    /// the first datagram.
    /// </summary>
    /// <param name="host">The UDP host; defaults to the stream session's address.</param>
    /// <param name="port">The UDP port; defaults to the port in the ticket.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="NetworkException">The ticket was refused, or the session could not be established in time.</exception>
    public override async Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            this.Teardown(raiseDisconnected: false);

            DatagramTicketInfo ticket = await this.RequestTicketAsync(ct).ConfigureAwait(false);
            this.Ticket = ticket;

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(string.IsNullOrWhiteSpace(host) ? this.Options.Address : host, ct).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new NetworkException($"Could not resolve host: {host ?? this.Options.Address}");
            }

            IPEndPoint remote = new(addresses[0], port ?? ticket.Port);
            Socket socket = new(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                TrySetDontFragment(socket);
                await socket.ConnectAsync(remote, ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            Span<byte> nonce = stackalloc byte[DatagramKeySchedule.TicketNonceSize];
            _ = BitConverter.TryWriteBytes(nonce, (ulong)ticket.Nonce);
            _ = BitConverter.TryWriteBytes(nonce[8..], (ulong)(ticket.Nonce >> 64));
#pragma warning disable CA2000 // Ownership passes to the engine, which disposes it.
            ChaCha20Poly1305DatagramProtection protection = ChaCha20Poly1305DatagramProtection.Create(this.State.Secret, ticket.ConnectionId, nonce, isServer: false);
#pragma warning restore CA2000
            nonce.Clear();

            DatagramEngineOptions options = new()
            {
                ChannelCount = ticket.ChannelCount,
                IdleTimeoutMs = ticket.IdleTimeoutMs,
                MaxMessageSize = ticket.MaxMessageSize,
                ReceiveBufferBytes = ticket.ReceiveBufferBytes,
                ChannelReceiveBufferBytes = ticket.ChannelReceiveBufferBytes,
            };

            TaskCompletionSource established = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_engineLock)
            {
                _socket = socket;
                _closeError = null;
                _established = established;
                _engine = new DatagramEngine(
                    options, new DatagramPeerLimits(ticket.ReceiveBufferBytes, ticket.ChannelReceiveBufferBytes),
                    protection, ticket.ConnectionId, isServer: false, new EngineHandler(this), DatagramEngine.NowMicros());
                _timer = new Timer(static state => ((DatagramSession)state!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
                _loopCts = new CancellationTokenSource();
                CancellationToken token = _loopCts.Token;
                _loopTask = Task.Factory.StartNew(() => this.ReceiveLoopAsync(socket, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
                this.FlushLocked(DatagramEngine.NowMicros());
            }

            // The first datagram must authenticate within the ticket lifetime.
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, (int)ticket.ExpiresInSeconds)));
            try
            {
                await established.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new NetworkException("The datagram session was not established before the ticket expired.");
            }

            this.OnConnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.Teardown(raiseDisconnected: false);
            this.OnError?.Invoke(this, ex);
            throw ex as NetworkException ?? new NetworkException($"Datagram session failed: {ex.Message}", ex);
        }
        finally
        {
            _ = _connectionLock.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        this.MarkIntentionalDisconnect();
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_engineLock)
            {
                if (_engine is { State: < DatagramEngineState.Draining } engine)
                {
                    long now = DatagramEngine.NowMicros();
                    engine.Close(DatagramCloseCode.NoError, null, now);
                    this.FlushLocked(now);
                }
            }

            this.Teardown(raiseDisconnected: true);
        }
        finally
        {
            _ = this.ConsumeIntentionalDisconnect();
            _ = _connectionLock.Release();
        }
    }

#pragma warning disable RS0026 // Overrides of the TransportSession contract; the overload set is inherited.
    /// <inheritdoc/>
    public override Task SendAsync(IPacket packet, CancellationToken ct = default)
        => this.SendWithDeliveryAsync(packet, DeliveryMethod.ReliableOrdered, 0, ct);

    /// <inheritdoc/>
    public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
        => this.SendWithDeliveryAsync(packet, DeliveryMethod.ReliableOrdered, 0, ct);

    /// <inheritdoc/>
    public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
    {
        this.Send(payload.Span, DeliveryMethod.ReliableOrdered, 0);
        return Task.CompletedTask;
    }
#pragma warning restore RS0026

    /// <summary>
    /// Sends a packet with a delivery method on a channel.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <param name="method">The delivery method.</param>
    /// <param name="channel">The channel, below the ticket's channel count.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes once the packet is queued.</returns>
    /// <exception cref="NetworkException">The session is closed, the packet is too large for the method, or the queue is full.</exception>
    public Task SendWithDeliveryAsync(IPacket packet, DeliveryMethod method, byte channel = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ct.ThrowIfCancellationRequested();

        _ = this.StampSequenceId(packet);
        using BufferLease lease = BufferLease.Rent(packet.Length);
        lease.CommitLength(packet.Serialize(lease.SpanFull));
        this.Send(lease.Span, method, channel);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a packet reliably and completes when the server acknowledged every fragment of it.
    /// </summary>
    /// <returns><see langword="true"/> when acknowledged; <see langword="false"/> when the session closed first.</returns>
    public Task<bool> SendWithReceiptAsync(IPacket packet, DeliveryMethod method = DeliveryMethod.ReliableOrdered, byte channel = 0)
    {
        ArgumentNullException.ThrowIfNull(packet);

        _ = this.StampSequenceId(packet);
        using BufferLease lease = BufferLease.Rent(packet.Length);
        lease.CommitLength(packet.Serialize(lease.SpanFull));

        lock (_engineLock)
        {
            DatagramEngine engine = _engine ?? throw new NetworkException("The datagram session is not connected.");
            long now = DatagramEngine.NowMicros();
            Task<bool> receipt = engine.TrySendWithReceipt(lease.Span, method, channel, 0, now)
                ?? throw new NetworkException($"The packet ({lease.Length} bytes) is too large for {method} or the send queue is full.");
            this.FlushLocked(now);
            return receipt;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Datagram frames carry no stream sequence numbers; nothing to reset.</remarks>
    public override void ResetSequenceCounters()
    {
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        this.MarkIntentionalDisconnect();
        this.Teardown(raiseDisconnected: true);
        _connectionLock.Dispose();
    }

    private async Task<DatagramTicketInfo> RequestTicketAsync(CancellationToken ct)
    {
        using PacketScope<DatagramTicket> scope = PacketFactory<DatagramTicket>.Acquire();
        DatagramTicket request = scope.Value;
        request.InitializeRequest();

        DatagramTicket response = await _stream.RequestAsync<DatagramTicket>(
            request,
            RequestOptions.Default.WithTimeout(Math.Max(1_000, this.Options.ConnectTimeoutMillis)),
            static r => r.Stage == DatagramTicketStage.RESPONSE,
            ct).ConfigureAwait(false);

        if (response.Reason != ProtocolReason.NONE || !response.Validate(out _))
        {
            throw new NetworkException($"The server refused the datagram ticket: {response.Reason}.");
        }

        return response.ToTicketInfo();
    }

    private void Send(ReadOnlySpan<byte> frame, DeliveryMethod method, byte channel)
    {
        lock (_engineLock)
        {
            DatagramEngine engine = _engine ?? throw new NetworkException("The datagram session is not connected.");
            long now = DatagramEngine.NowMicros();
            if (!engine.TrySend(frame, method, channel, 0, now))
            {
                throw new NetworkException(engine.State >= DatagramEngineState.Draining
                    ? "The datagram session is closed."
                    : $"The frame ({frame.Length} bytes) is too large for {method} or the send queue of channel {channel} is full.");
            }

            this.FlushLocked(now);
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(DatagramEngine.MaxDatagramSize + 1, pinned: true);
        while (!ct.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.MessageSize)
            {
                continue; // ICMP errors are not authenticated: ignore them.
            }

            lock (_engineLock)
            {
                if (_engine is not { } engine)
                {
                    return;
                }

                long now = DatagramEngine.NowMicros();
                if (engine.Receive(buffer.AsSpan(0, received), from: null, now) && engine.State == DatagramEngineState.Established)
                {
                    _ = _established?.TrySetResult();
                }

                this.FlushLocked(now);
            }

            this.DispatchDeliveries();
        }
    }

    private void OnTimer()
    {
        lock (_engineLock)
        {
            if (_engine is not { } engine)
            {
                return;
            }

            _timerDue = long.MaxValue;
            long now = DatagramEngine.NowMicros();
            engine.OnTimer(now);
            this.FlushLocked(now);
        }

        this.DispatchDeliveries();
    }

    private void FlushLocked(long now)
    {
        if (_engine is not { } engine || _socket is not { } socket)
        {
            return;
        }

        int length;
        while ((length = engine.Poll(_sendBuffer, now, out _)) > 0)
        {
            try
            {
                _ = socket.Send(_sendBuffer.AsSpan(0, length), SocketFlags.None);
            }
            catch (SocketException)
            {
                // Best effort: the engine retransmits what matters.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        long next = engine.NextTimer;
        if (next != long.MaxValue && next < _timerDue)
        {
            _timerDue = next;
            _ = _timer?.Change(Math.Max(0, (next - now + 999) / 1000), Timeout.Infinite);
        }
    }

    /// <summary>Runs message and close handlers outside the engine lock.</summary>
    private void DispatchDeliveries()
    {
        (BufferLease Message, DeliveryMethod Method, byte Channel)[] batch;
        Exception? closed;
        lock (_engineLock)
        {
            if (_deliveries.Count == 0 && _closeError is null)
            {
                return;
            }

            batch = [.. _deliveries];
            _deliveries.Clear();
            closed = _closeError;
            _closeError = null;
        }

        foreach ((BufferLease message, DeliveryMethod _, byte _) in batch)
        {
            IBufferLease current = message;
            try
            {
                if (FramePipeline.TryProcessInbound(ref current, this.State.Secret.AsSpan(), this.Options.Algorithm, out _))
                {
                    this.DispatchPendingMessage(current);
                    this.OnMessageReceived?.Invoke(this, current);
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                this.OnError?.Invoke(this, ex);
            }
            finally
            {
                if (!ReferenceEquals(current, message))
                {
                    current.Dispose();
                }

                message.Dispose();
            }
        }

        if (closed is not null)
        {
            _ = Task.Run(() => this.Teardown(raiseDisconnected: true, closed));
        }
    }

    private void Teardown(bool raiseDisconnected, Exception? reason = null)
    {
        Socket? socket;
        CancellationTokenSource? cts;
        DatagramEngine? engine;
        lock (_engineLock)
        {
            socket = _socket;
            cts = _loopCts;
            engine = _engine;
            _socket = null;
            _loopCts = null;
            _loopTask = null;
            _engine = null;
            _timer?.Dispose();
            _timer = null;
            _timerDue = long.MaxValue;
            _ = _established?.TrySetCanceled();

            foreach ((BufferLease message, DeliveryMethod _, byte _) in _deliveries)
            {
                message.Dispose();
            }

            _deliveries.Clear();
        }

        cts?.Cancel();
        socket?.Dispose();
        engine?.Dispose();
        cts?.Dispose();

        if (raiseDisconnected && socket is not null)
        {
            NetworkException error = reason is null
                ? new("The datagram session was disconnected.")
                : new("The datagram session was disconnected.", reason);
            this.DispatchPendingDisconnect(error);
            this.OnDisconnected?.Invoke(this, error);
        }
    }

    private static void TrySetDontFragment(Socket socket)
    {
        try
        {
            socket.DontFragment = true;
        }
        catch (SocketException)
        {
            // Probes may then be fragmented; the session still works.
        }
        catch (NotSupportedException)
        {
            // Same: the platform does not support the option for this socket.
        }
    }

    /// <summary>Collects engine output; runs under the engine lock.</summary>
    private sealed class EngineHandler(DatagramSession owner) : IDatagramEngineHandler
    {
        public void OnMessage(BufferLease message, DeliveryMethod method, byte channel)
            => owner._deliveries.Add((message, method, channel));

        public void OnClosed(DatagramCloseCode code, ulong applicationCode, string reason, bool remote)
        {
            if (code is DatagramCloseCode.NoError && !remote)
            {
                return; // Our own DisconnectAsync tears the session down.
            }

            owner._closeError = new NetworkException($"The datagram session closed: {code} {reason}".TrimEnd());
        }
    }
}
