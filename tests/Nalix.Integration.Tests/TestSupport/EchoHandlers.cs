// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.Pooling;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// Server-side handler for <see cref="EchoPacket"/>, mapped through the normal Hosting pipeline.
/// </summary>
[PacketHandler("Integration.EndToEnd.Echo")]
public static class EchoHandlers
{
    [PacketOpcode(EchoPacket.OpCode)]
    public static async ValueTask HandleEchoAsync(IPacketContext<EchoPacket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using PacketScope<EchoPacket> lease = PacketFactory<EchoPacket>.Acquire();
        EchoPacket response = lease.Value;
        response.Sequence = context.Packet.Sequence + 1;
        response.Text = context.Packet.Text.ToUpperInvariant();

        await context.Sender.ReplyAsync(response).ConfigureAwait(false);
    }
}
