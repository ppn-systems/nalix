// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Abstractions.Networking.Datagrams;

/// <summary>
/// Issues datagram session tickets for established stream connections. Implemented by the datagram listener.
/// </summary>
public interface IDatagramTicketIssuer
{
    /// <summary>
    /// Issues a single-use ticket. The stream connection's secret, identity and permission level are
    /// snapshotted now; later changes to the stream connection do not affect the datagram session.
    /// </summary>
    /// <param name="streamConnection">The established TCP or WebSocket connection.</param>
    /// <param name="onAccepted">
    /// Runs on the new datagram connection once its first datagram authenticated, before it is registered and
    /// dispatched (for example to copy runtime state).
    /// </param>
    /// <returns>The ticket to send to the client.</returns>
    DatagramTicketInfo IssueTicket(IConnection streamConnection, Action<IConnection>? onAccepted = null);
}

/// <summary>
/// The values of a datagram session ticket.
/// </summary>
/// <param name="ConnectionId">The datagram connection identifier (from the CSPRNG).</param>
/// <param name="Nonce">The 16-byte ticket nonce that makes the session keys fresh.</param>
/// <param name="Port">The UDP port to send to; the host is the stream session's host.</param>
/// <param name="ExpiresInSeconds">Seconds within which the first datagram must arrive.</param>
/// <param name="IdleTimeoutMs">The idle timeout of the session.</param>
/// <param name="MaxMessageSize">The largest message either side accepts.</param>
/// <param name="ChannelCount">The number of channels.</param>
/// <param name="ReceiveBufferBytes">The total bytes the server buffers for ordering and reassembly.</param>
/// <param name="ChannelReceiveBufferBytes">The bytes the server buffers per channel.</param>
public readonly record struct DatagramTicketInfo(
    ulong ConnectionId,
    UInt128 Nonce,
    ushort Port,
    ushort ExpiresInSeconds,
    int IdleTimeoutMs,
    int MaxMessageSize,
    byte ChannelCount,
    long ReceiveBufferBytes,
    long ChannelReceiveBufferBytes);
