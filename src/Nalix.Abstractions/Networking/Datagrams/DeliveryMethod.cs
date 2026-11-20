// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Abstractions.Networking.Datagrams;

/// <summary>
/// How a message sent over a datagram session is delivered. Ordering and sequencing are per channel.
/// Stream transports (TCP, WebSocket) deliver every method reliably and in order.
/// </summary>
public enum DeliveryMethod : byte
{
    /// <summary>
    /// Delivered at most once, in any order; never retransmitted.
    /// </summary>
    Unreliable = 0,

    /// <summary>
    /// Delivered at most once; a message older than the newest one already delivered on its channel is dropped.
    /// Never retransmitted.
    /// </summary>
    UnreliableSequenced = 1,

    /// <summary>
    /// Delivered exactly once, in any order.
    /// </summary>
    ReliableUnordered = 2,

    /// <summary>
    /// Delivered exactly once, in send order within its channel.
    /// </summary>
    ReliableOrdered = 3,

    /// <summary>
    /// The newest message of the channel is guaranteed to arrive; older ones may be skipped. Only the newest
    /// message is retransmitted.
    /// </summary>
    ReliableSequenced = 4,
}
