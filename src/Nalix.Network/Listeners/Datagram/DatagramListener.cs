// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Primitives;
using Nalix.Environment.Configuration;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Random;
using Nalix.Network.Connections;
using Nalix.Network.Options;

namespace Nalix.Network.Listeners.Datagram;

/// <summary>
/// UDP listener of the Nalix Datagram Protocol. It issues tickets to established stream connections
/// (<see cref="IDatagramTicketIssuer"/>), routes datagrams to their session by connection identifier, and turns
/// a ticket into a <see cref="DatagramConnection"/> when its first datagram authenticates.
/// </summary>
/// <remarks>
/// Unauthenticated datagrams never get an answer: an unknown connection identifier costs one table lookup, a
/// known one an AEAD open. Crypto is injected as an <see cref="IDatagramProtectionFactory"/>, so this assembly
/// does not reference the codec.
/// </remarks>
public sealed class DatagramListener : IListener, IDatagramTicketIssuer
{
    private readonly ushort _port;
    private readonly IProtocol _protocol;
    private readonly IConnectionHub _hub;
    private readonly IDatagramProtectionFactory _protectionFactory;
    private readonly DatagramOptions _options;
    private readonly DatagramEngineOptions _engineOptions;
    private readonly ConcurrentDictionary<ulong, DatagramConnection> _sessions = new();
    private readonly ConcurrentDictionary<ulong, PendingTicket> _pending = new();
    private readonly Lock _gate = new();

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Timer? _sweepTimer;
    private long _datagramsReceived;
    private long _datagramsDropped;
    private long _ticketsIssued;

    /// <summary>
    /// Initializes the listener.
    /// </summary>
    /// <param name="port">The UDP port; 0 picks a free port (see <see cref="Port"/>).</param>
    /// <param name="protocol">The protocol whose frame processor receives the messages of every session.</param>
    /// <param name="hub">The hub sessions are registered in.</param>
    /// <param name="protectionFactory">Creates the AEAD protection of each session.</param>
    /// <param name="options">Settings; defaults to the configured <see cref="DatagramOptions"/>.</param>
    public DatagramListener(ushort port, IProtocol protocol, IConnectionHub hub, IDatagramProtectionFactory protectionFactory, DatagramOptions? options = null)
    {
        _port = port;
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _protectionFactory = protectionFactory ?? throw new ArgumentNullException(nameof(protectionFactory));
        _options = options ?? ConfigurationManager.Instance.Get<DatagramOptions>();
        _options.Validate();
        _engineOptions = _options.ToEngineOptions();
    }

    /// <summary>
    /// Gets the bound UDP port, or the configured one before activation.
    /// </summary>
    public ushort Port => _socket?.LocalEndPoint is IPEndPoint endPoint ? (ushort)endPoint.Port : _port;

    /// <summary>Gets the number of established sessions.</summary>
    public int SessionCount => _sessions.Count;

    /// <inheritdoc/>
    public void Activate(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_socket is not null)
            {
                return;
            }

            Socket socket = CreateSocket();
            try
            {
                socket.ReceiveBufferSize = _options.SocketReceiveBufferSize;
                socket.SendBufferSize = _options.SocketSendBufferSize;
                TrySetDontFragment(socket);
                socket.Bind(new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, _port));
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _cts.Token;
            _receiveLoop = Task.Factory.StartNew(
                () => this.ReceiveLoopAsync(socket, token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            _sweepTimer = new Timer(static state => ((DatagramListener)state!).SweepExpiredTickets(), this, 1000, 1000);
        }
    }

    /// <inheritdoc/>
    public void Deactivate(CancellationToken cancellationToken = default)
    {
        Socket? socket;
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            socket = _socket;
            cts = _cts;
            loop = _receiveLoop;
            _socket = null;
            _cts = null;
            _receiveLoop = null;
            _sweepTimer?.Dispose();
            _sweepTimer = null;
        }

        if (socket is null)
        {
            return;
        }

        cts?.Cancel();

        foreach (DatagramConnection connection in _sessions.Values)
        {
            connection.Disconnect("Server is shutting down.");
        }

        foreach (PendingTicket pending in _pending.Values)
        {
            pending.Discard();
        }

        _pending.Clear();
        socket.Dispose();

        try
        {
            _ = loop?.Wait(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // The loop ends with an exception because its socket was closed.
        }

        cts?.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => this.Deactivate();

    /// <inheritdoc/>
    public DatagramTicketInfo IssueTicket(IConnection streamConnection, Action<IConnection>? onAccepted = null)
    {
        ArgumentNullException.ThrowIfNull(streamConnection);

        if (_socket is null)
        {
            throw new InvalidOperationException("The datagram listener is not active.");
        }

        if (streamConnection is DatagramConnection)
        {
            throw new InvalidOperationException("A datagram session cannot issue a datagram ticket.");
        }

        if (streamConnection.Secret.IsZero)
        {
            throw new InvalidOperationException("The stream connection has no session secret; complete the handshake first.");
        }

        if (_sessions.Count + _pending.Count >= _options.MaxSessions)
        {
            throw new InvalidOperationException("The datagram session limit is reached.");
        }

        Span<byte> nonce = stackalloc byte[16];
        Csprng.Fill(nonce);
        UInt128 nonceValue = new(BitConverter.ToUInt64(nonce[8..]), BitConverter.ToUInt64(nonce));

        PendingTicket ticket = new(
            new DatagramIdentity(streamConnection.UserId, streamConnection.Level, streamConnection.Algorithm, streamConnection.Secret),
            nonce.ToArray(),
            System.Environment.TickCount64 + (_options.TicketLifetimeSeconds * 1000L),
            onAccepted);

        ulong connectionId;
        do
        {
            // Not a Snowflake: the identifier must not be predictable.
            connectionId = Csprng.NextUInt64();
        }
        while (connectionId == 0 || _sessions.ContainsKey(connectionId) || !_pending.TryAdd(connectionId, ticket));

        nonce.Clear();
        _ = Interlocked.Increment(ref _ticketsIssued);

        return new DatagramTicketInfo(
            connectionId, nonceValue, this.Port, (ushort)_options.TicketLifetimeSeconds, _options.IdleTimeoutMs,
            _options.MaxMessageSize, (byte)_options.ChannelCount, _options.ReceiveBufferBytes, _options.ChannelReceiveBufferBytes);
    }

    /// <inheritdoc/>
    public string GenerateReport()
        => $"DatagramListener port={this.Port} sessions={_sessions.Count} pending={_pending.Count} tickets={Interlocked.Read(ref _ticketsIssued)} " +
           $"received={Interlocked.Read(ref _datagramsReceived)} dropped={Interlocked.Read(ref _datagramsDropped)}";

    /// <inheritdoc/>
    public void WriteReportData(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber("port", this.Port);
        writer.WriteNumber("sessions", _sessions.Count);
        writer.WriteNumber("pendingTickets", _pending.Count);
        writer.WriteNumber("ticketsIssued", Interlocked.Read(ref _ticketsIssued));
        writer.WriteNumber("datagramsReceived", Interlocked.Read(ref _datagramsReceived));
        writer.WriteNumber("datagramsDropped", Interlocked.Read(ref _datagramsDropped));
        writer.WriteEndObject();
    }

    internal void SendTo(ReadOnlySpan<byte> datagram, SocketAddress target)
    {
        Socket? socket = _socket;
        if (socket is null)
        {
            return;
        }

        try
        {
            _ = socket.SendTo(datagram, SocketFlags.None, target);
        }
        catch (SocketException)
        {
            // UDP is best effort; the engine recovers from the loss.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void OnConnectionClosed(DatagramConnection connection)
        => _ = _sessions.TryRemove(new KeyValuePair<ulong, DatagramConnection>(connection.DatagramConnectionId, connection));

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(DatagramEngine.MaxDatagramSize + 1, pinned: true);
        SocketAddress from = new(socket.AddressFamily);

        while (!cancellationToken.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }

            _ = Interlocked.Increment(ref _datagramsReceived);
            try
            {
                if (!this.Route(buffer.AsSpan(0, received), from))
                {
                    _ = Interlocked.Increment(ref _datagramsDropped);
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                _ = Interlocked.Increment(ref _datagramsDropped);
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.DatagramListener:Receive", "datagram processing failed", ex));
                }
            }
        }
    }

    private bool Route(ReadOnlySpan<byte> datagram, SocketAddress from)
    {
        if (datagram.Length > DatagramEngine.MaxDatagramSize || !DatagramEngine.TryReadConnectionId(datagram, out ulong connectionId))
        {
            return false;
        }

        if (_sessions.TryGetValue(connectionId, out DatagramConnection? connection))
        {
            return connection.OnDatagram(datagram, from);
        }

        return _pending.TryGetValue(connectionId, out PendingTicket? ticket) && this.TryAccept(connectionId, ticket, datagram, from);
    }

    private bool TryAccept(ulong connectionId, PendingTicket ticket, ReadOnlySpan<byte> datagram, SocketAddress from)
    {
        if (System.Environment.TickCount64 > ticket.ExpiresAtMs)
        {
            if (_pending.TryRemove(new KeyValuePair<ulong, PendingTicket>(connectionId, ticket)))
            {
                ticket.Discard();
            }

            return false;
        }

        // The connection is created on the first datagram with a known identifier; it stays pending until one
        // authenticates, so a forged datagram cannot consume the ticket.
        DatagramConnection connection = ticket.GetOrCreateConnection(this, connectionId, _protectionFactory, _engineOptions, _protocol.OpCodeExtractor);
        if (!connection.OnDatagram(datagram, from) || !connection.IsEstablished)
        {
            return false;
        }

        if (!_pending.TryRemove(new KeyValuePair<ulong, PendingTicket>(connectionId, ticket)))
        {
            return true; // Another datagram promoted it concurrently.
        }

        ticket.ClearSecret();
        _ = _sessions.TryAdd(connectionId, connection);
        this.Accept(connection, ticket.OnAccepted);
        return true;
    }

    private void Accept(DatagramConnection connection, Action<IConnection>? onAccepted)
    {
        try
        {
            onAccepted?.Invoke(connection);

            connection.ConnectionClosed += this.HandleConnectionClosed;
            connection.MessageProcessed += _protocol.PostProcessMessage;
            connection.MessageProcessing += _protocol.FrameProcessor.ProcessFrame;

            _protocol.OnAccept(connection, _cts?.Token ?? CancellationToken.None);

            if (!connection.IsDisposed)
            {
                _hub.RegisterConnection(connection);
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.DatagramListener:Accept", "accept failed", ex));
            }

            connection.Dispose();
        }
    }

    private void HandleConnectionClosed(object? sender, IConnectionEventArgs args)
    {
        if (args?.Connection is not DatagramConnection connection)
        {
            return;
        }

        connection.ConnectionClosed -= this.HandleConnectionClosed;
        connection.MessageProcessed -= _protocol.PostProcessMessage;
        connection.MessageProcessing -= _protocol.FrameProcessor.ProcessFrame;
    }

    private void SweepExpiredTickets()
    {
        long now = System.Environment.TickCount64;
        foreach (KeyValuePair<ulong, PendingTicket> entry in _pending)
        {
            if (now > entry.Value.ExpiresAtMs && _pending.TryRemove(entry))
            {
                entry.Value.Discard();
            }
        }
    }

    private static Socket CreateSocket()
    {
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                return new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                // IPv6 is disabled on this host.
            }
        }

        return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    }

    private static void TrySetDontFragment(Socket socket)
    {
        // Path MTU discovery needs datagrams that are dropped, not fragmented, when they are too large.
        try
        {
            socket.DontFragment = true;
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        {
            // Not supported for this socket type on this platform: probes still work, they may be fragmented.
        }
    }

#pragma warning disable CA1001 // The connection is disposed by Discard() or handed over to the session table.
    /// <summary>A ticket waiting for its first authenticated datagram.</summary>
    private sealed class PendingTicket(DatagramIdentity identity, byte[] nonce, long expiresAtMs, Action<IConnection>? onAccepted)
    {
        private readonly Lock _lock = new();
        private DatagramIdentity _identity = identity;
        private DatagramConnection? _connection;

        public long ExpiresAtMs { get; } = expiresAtMs;

        public Action<IConnection>? OnAccepted { get; } = onAccepted;

        public DatagramConnection GetOrCreateConnection(
            DatagramListener listener, ulong connectionId, IDatagramProtectionFactory factory,
            DatagramEngineOptions options, Abstractions.Networking.Protocols.IOpCodeExtractor classifier)
        {
            lock (_lock)
            {
                if (_connection is null)
                {
                    IDatagramProtection protection = factory.Create(_identity.Secret, connectionId, nonce, isServer: true);
                    _connection = new DatagramConnection(listener, connectionId, protection, options, classifier, _identity);
                }

                return _connection;
            }
        }

        public void ClearSecret()
        {
            lock (_lock)
            {
                _identity = _identity with { Secret = Bytes32.Zero };
                Array.Clear(nonce);
            }
        }

        public void Discard()
        {
            lock (_lock)
            {
                _connection?.Dispose();
                _connection = null;
                _identity = _identity with { Secret = Bytes32.Zero };
                Array.Clear(nonce);
            }
        }
    }
#pragma warning restore CA1001
}
