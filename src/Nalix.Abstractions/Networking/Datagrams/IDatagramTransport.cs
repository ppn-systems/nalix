// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nalix.Abstractions.Networking.Datagrams;

/// <summary>
/// The transport of a datagram session. Sends through <see cref="IConnection.ITransport"/> use
/// <see cref="DeliveryMethod.ReliableOrdered"/> on channel 0.
/// </summary>
/// <remarks>
/// Every datagram is protected by the session AEAD and replay window, so frames received on this transport
/// are treated as encrypted on the wire and their inner sequence numbers are not checked again.
/// </remarks>
public interface IDatagramTransport : IConnection.ITransport
{
    /// <summary>
    /// Sends a message with a delivery method on a channel.
    /// </summary>
    /// <param name="message">The message (a Nalix frame); it is copied before the call returns.</param>
    /// <param name="method">The delivery method.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes once the message is queued.</returns>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, DeliveryMethod method, byte channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a view of this transport whose sends use <paramref name="method"/> on <paramref name="channel"/>.
    /// Views are cached, so repeated calls do not allocate.
    /// </summary>
    /// <param name="method">The delivery method.</param>
    /// <param name="channel">The channel.</param>
    /// <returns>The transport view.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The method is unknown or the channel is not below the session's channel count.</exception>
    IConnection.ITransport WithDelivery(DeliveryMethod method, byte channel);
}
