// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;
using Nalix.Environment.Memory;

namespace Nalix.Comparison.Benchmarks.Libraries;

/// <summary>Echo request carrying an opaque payload held in a pooled buffer (no <c>byte[]</c> per message).</summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoRequestPacket : PacketBase<EchoRequestPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 0x7301;

    [SerializeOrder(0)]
    public BufferLease? Data { get; set; }

    public override void ResetForPool()
    {
        base.ResetForPool();
        this.Data?.Dispose();
        this.Data = null;
    }
}

/// <summary>Echo response carrying the same payload back, in a pooled buffer.</summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoResponsePacket : PacketBase<EchoResponsePacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 0x7302;

    [SerializeOrder(0)]
    public BufferLease? Data { get; set; }

    public override void ResetForPool()
    {
        base.ResetForPool();
        this.Data?.Dispose();
        this.Data = null;
    }
}

/// <summary>Plaintext echo handler (recommended pooled-response pattern from the samples).</summary>
[PacketHandler("Bench.Echo")]
public static class EchoHandlers
{
    [PacketOpcode(0x7301)]
    [PacketPermission(PermissionLevel.NONE)]
    public static async ValueTask EchoAsync(IPacketContext<EchoRequestPacket> context)
    {
        using PacketScope<EchoResponsePacket> lease = PacketFactory<EchoResponsePacket>.Acquire();
        // The request keeps its reference until the dispatcher returns it; the response takes its own.
        context.Packet.Data!.Retain();
        lease.Value.Data = context.Packet.Data;
        await context.Sender.ReplyAsync(lease.Value).ConfigureAwait(false);
    }
}

/// <summary>Encrypted echo handler: response is AEAD-encrypted (request is sent encrypted by the client).</summary>
[PacketHandler("Bench.EchoEncrypted")]
public static class EchoEncryptedHandlers
{
    [PacketOpcode(0x7301)]
    [PacketPermission(PermissionLevel.NONE)]
    [PacketEncryption(true)]
    public static async ValueTask EchoAsync(IPacketContext<EchoRequestPacket> context)
    {
        using PacketScope<EchoResponsePacket> lease = PacketFactory<EchoResponsePacket>.Acquire();
        // The request keeps its reference until the dispatcher returns it; the response takes its own.
        context.Packet.Data!.Retain();
        lease.Value.Data = context.Packet.Data;
        await context.Sender.ReplyAsync(lease.Value).ConfigureAwait(false);
    }
}
