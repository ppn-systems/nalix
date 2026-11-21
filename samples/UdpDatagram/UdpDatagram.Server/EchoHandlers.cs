// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.Pooling;
using UdpDatagram.Contracts;

namespace UdpDatagram.Server;

/// <summary>
/// Handlers that receive their packets over UDP.
/// <para>
/// The handler shape is identical to a TCP handler; the transport is invisible to it.
/// </para>
/// </summary>
[PacketHandler("UdpDatagram.Echo")]
public static class EchoHandlers
{
    /// <summary>
    /// Replies to an <see cref="EchoRequestPacket"/> with its sequence plus one.
    /// </summary>
    /// <param name="context">The packet context carrying the request and the sender.</param>
    [PacketOpcode(0x7301)]
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
    [PacketOpcode(0x7303)]
    public static ValueTask HandleTelemetryAsync(IPacketContext<TelemetryPacket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Console.WriteLine($"  telemetry #{context.Packet.Sequence} received");
        return ValueTask.CompletedTask;
    }
}
