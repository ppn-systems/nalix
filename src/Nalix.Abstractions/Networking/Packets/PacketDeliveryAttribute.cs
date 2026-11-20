// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Networking.Datagrams;

namespace Nalix.Abstractions.Networking.Packets;

/// <summary>
/// Selects the delivery method and channel of the packets a handler sends on a datagram connection.
/// Without it, datagram sends use <see cref="DeliveryMethod.ReliableOrdered"/> on channel 0. Stream
/// connections ignore it: they always deliver reliably and in order.
/// </summary>
/// <param name="method">The delivery method.</param>
/// <param name="channel">The channel.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class PacketDeliveryAttribute(DeliveryMethod method, byte channel = 0) : Attribute
{
    /// <summary>
    /// Gets the delivery method.
    /// </summary>
    public DeliveryMethod Method { get; } = method;

    /// <summary>
    /// Gets the channel.
    /// </summary>
    public byte Channel { get; } = channel;
}
