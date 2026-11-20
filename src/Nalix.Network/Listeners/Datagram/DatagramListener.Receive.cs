// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Datagrams;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Datagram;

namespace Nalix.Network.Listeners.Datagram;

public sealed partial class DatagramListener
{
    #region Receive

    /// <summary>
    /// Receive worker: reads the socket until the listener stops. Each worker owns its buffer and source address,
    /// so the loop does not allocate per datagram.
    /// </summary>
    private async ValueTask ReceiveAsync(Socket socket, IWorkerContext ctx, CancellationToken cancellationToken)
    {
        // One byte more than the largest valid datagram, so an oversized one is detected instead of truncated.
        byte[] buffer = GC.AllocateUninitializedArray<byte>(DatagramEngine.MaxDatagramSize + 1, pinned: true);
        SocketAddress from = new(socket.AddressFamily);
        int consecutiveErrors = 0;

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
                // ICMP port unreachable (Windows) or a datagram larger than the buffer: not fatal for a UDP socket.
                this.Metrics.RECORD_DROPPED_MALFORMED();
                continue;
            }
            catch (SocketException ex)
            {
                this.Metrics.RECORD_ERROR();
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error,
                        new DiagnosticLog("NW.DatagramListener:Receive", $"receive-failed port={_port} error={ex.SocketErrorCode}", ex));
                }

                // A persistent error (network down, socket in an error state) fails every call at once: back off
                // instead of spinning, up to one second between attempts.
                try
                {
                    await Task.Delay(Math.Min(1000, 10 * ++consecutiveErrors), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            consecutiveErrors = 0;
            ctx.Beat();
            this.Metrics.RECORD_RECEIVED(received);

            try
            {
                this.Route(buffer.AsSpan(0, received), from);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                this.Metrics.RECORD_ERROR();
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error,
                        new DiagnosticLog("NW.DatagramListener:Receive", "datagram processing failed", ex));
                }
            }
        }
    }

    /// <summary>Hands a datagram to its session, or to its pending ticket.</summary>
    private void Route(ReadOnlySpan<byte> datagram, SocketAddress from)
    {
        if (datagram.Length > DatagramEngine.MaxDatagramSize || !DatagramEngine.TryReadConnectionId(datagram, out ulong connectionId))
        {
            this.Metrics.RECORD_DROPPED_MALFORMED();
            return;
        }

        bool authenticated;
        if (_sessions.TryGetValue(connectionId, out DatagramConnection? connection))
        {
            authenticated = connection.OnDatagram(datagram, from);
        }
        else if (_pending.TryGetValue(connectionId, out PendingTicket? ticket))
        {
            authenticated = this.TryPromote(connectionId, ticket, datagram, from);
        }
        else
        {
            this.Metrics.RECORD_DROPPED_UNKNOWN();
            return;
        }

        if (!authenticated)
        {
            this.Metrics.RECORD_DROPPED_UNAUTHENTICATED();
        }
    }

    #endregion Receive

    #region Accept

    /// <summary>
    /// Feeds a datagram to the pending connection of a ticket and, when it authenticates, moves the connection to
    /// the session table and accepts it.
    /// </summary>
    private bool TryPromote(ulong connectionId, PendingTicket ticket, ReadOnlySpan<byte> datagram, SocketAddress from)
    {
        if (ticket.IsExpired(System.Environment.TickCount64))
        {
            this.DiscardTicket(connectionId, ticket);
            return false;
        }

        DatagramConnection connection = ticket.GetOrCreateConnection(this, connectionId, _protectionFactory, _engineOptions, _protocol.OpCodeExtractor);
        if (!connection.OnDatagram(datagram, from) || !connection.IsEstablished)
        {
            return false;
        }

        if (!_pending.TryRemove(new KeyValuePair<ulong, PendingTicket>(connectionId, ticket)))
        {
            return true; // Another worker promoted it concurrently.
        }

        _ = _ticketByStream.TryRemove(new KeyValuePair<ulong, ulong>(ticket.StreamConnectionId, connectionId));

        ticket.ClearSecret();

        IPEndPoint remote = DatagramSocketConnection.ToEndPoint(from);
        if (_guard is not null && !_guard.TryAccept(remote))
        {
            this.Metrics.RECORD_GUARD_REJECTION();
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog("NW.DatagramListener:Accept", $"guard-rejected remote={remote}"));
            }

            connection.Dispose();
            return true;
        }

        if (_guard is not null)
        {
            // The session owns the slot from here; it is released when the session is disposed (OnConnectionClosed),
            // which may already have happened: the engine can close the session from another thread at any time.
            connection.AttachGuardSlot(remote);
            if (connection.IsDisposed)
            {
                this.ReleaseGuardSlot(connection);
                return true;
            }
        }

        _ = _sessions.TryAdd(connectionId, connection);
        this.Accept(connection, ticket.OnAccepted);
        return true;
    }

    private void Accept(DatagramConnection connection, Action<IConnection>? onAccepted)
    {
        try
        {
            connection.ConnectionClosed += this.HandleConnectionClosed;
            connection.MessageProcessed += _protocol.PostProcessMessage;
            connection.MessageProcessing += _protocol.FrameProcessor.ProcessFrame;

            onAccepted?.Invoke(connection);
            _protocol.OnAccept(connection, _cts?.Token ?? CancellationToken.None);

            if (!connection.IsDisposed)
            {
                _hub.RegisterConnection(connection);
            }

            this.Metrics.RECORD_ACCEPTED();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.Metrics.RECORD_ERROR();
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog("NW.DatagramListener:Accept", "accept failed", ex));
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

    #endregion Accept

    #region Session Plumbing

    /// <summary>Sends a datagram produced by a session. UDP is best effort; the engine recovers from loss.</summary>
    internal void SendTo(ReadOnlySpan<byte> datagram, SocketAddress target)
    {
        Socket? socket = Volatile.Read(ref _socket);
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
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Removes a disposed session from the routing table and releases its guard slot. Called once per session.</summary>
    internal void OnConnectionClosed(DatagramConnection connection)
    {
        _ = _sessions.TryRemove(new KeyValuePair<ulong, DatagramConnection>(connection.DatagramConnectionId, connection));
        this.ReleaseGuardSlot(connection);
    }

    private void ReleaseGuardSlot(DatagramConnection connection)
    {
        if (connection.DetachGuardSlot() is { } endPoint)
        {
            _guard?.Release(endPoint);
        }
    }

    private void SweepExpiredTickets()
    {
        long now = System.Environment.TickCount64;
        foreach (KeyValuePair<ulong, PendingTicket> entry in _pending)
        {
            if (entry.Value.IsExpired(now))
            {
                this.DiscardTicket(entry.Key, entry.Value);
            }
        }
    }

    private void DiscardTicket(ulong connectionId, PendingTicket ticket)
    {
        if (this.RemoveTicket(connectionId, ticket))
        {
            this.Metrics.RECORD_TICKET_EXPIRED();
        }
    }

    /// <summary>Removes a pending ticket and wipes it; returns <see langword="false"/> when it was already gone.</summary>
    private bool RemoveTicket(ulong connectionId, PendingTicket ticket)
    {
        if (!_pending.TryRemove(new KeyValuePair<ulong, PendingTicket>(connectionId, ticket)))
        {
            return false;
        }

        _ = _ticketByStream.TryRemove(new KeyValuePair<ulong, ulong>(ticket.StreamConnectionId, connectionId));
        ticket.Discard();
        return true;
    }

    #endregion Session Plumbing
}
