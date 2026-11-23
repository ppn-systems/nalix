// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using UdpDatagram.Contracts;

namespace UdpDatagram.Server;

/// <summary>
/// Handlers that receive their packets over the datagram (UDP) transport.
/// <para>
/// The handler shape is identical to a TCP handler; <see cref="PacketDeliveryAttribute"/> only
/// chooses how the reply travels on a datagram connection.
/// </para>
/// <para>
/// Handlers require <see cref="PermissionLevel.USER"/> by default. A connection that only completed the
/// handshake has <see cref="PermissionLevel.NONE"/>, so these demo handlers lower the requirement
/// explicitly; a real server would authenticate the user and raise the connection's level instead.
/// </para>
/// </summary>
[PacketHandler("UdpDatagram.Echo")]
public static class EchoHandlers
{
    /// <summary>
    /// Replies to an <see cref="EchoRequestPacket"/> with its sequence plus one.
    /// The reply is retransmitted until acknowledged and delivered in order.
    /// </summary>
    /// <param name="context">The packet context carrying the request and the sender.</param>
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(0x7401)]
    [PacketDelivery(DeliveryMethod.ReliableOrdered)]
    public static async ValueTask HandleEchoAsync(IPacketContext<EchoRequestPacket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using PacketScope<EchoResponsePacket> lease = PacketFactory<EchoResponsePacket>.Acquire();
        lease.Value.Sequence = context.Packet.Sequence + 1;

        // ReplyAsync copies the request SequenceId so the client's RequestAsync can match it.
        await context.Sender.ReplyAsync(lease.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// Consumes a <see cref="TelemetryPacket"/>. No reply is sent, so a lost datagram costs nothing.
    /// </summary>
    /// <param name="context">The packet context carrying the telemetry sample.</param>
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(0x7403)]
    public static ValueTask HandleTelemetryAsync(IPacketContext<TelemetryPacket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Console.WriteLine($"  telemetry #{context.Packet.Sequence} received");
        return ValueTask.CompletedTask;
    }
}
