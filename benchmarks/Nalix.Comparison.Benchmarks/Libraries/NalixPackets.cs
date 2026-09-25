// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;

namespace Nalix.Comparison.Benchmarks.Libraries;

/// <summary>Echo request carrying an opaque byte payload.</summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoRequestPacket : PacketBase<EchoRequestPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 0x7301;

    [SerializeOrder(0)]
    [SerializeDynamicSize(1024)]
    public byte[] Data { get; set; } = [];
}

/// <summary>Echo response carrying the same payload back.</summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoResponsePacket : PacketBase<EchoResponsePacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 0x7302;

    [SerializeOrder(0)]
    [SerializeDynamicSize(1024)]
    public byte[] Data { get; set; } = [];
}

/// <summary>Plaintext echo handler (recommended pooled-response pattern from the samples).</summary>
[PacketHandler("Bench.Echo")]
public static class EchoHandlers
{
    [PacketOpcode(0x7301)]
    public static async ValueTask EchoAsync(IPacketContext<EchoRequestPacket> context)
    {
        using PacketScope<EchoResponsePacket> lease = PacketFactory<EchoResponsePacket>.Acquire();
        lease.Value.Data = context.Packet.Data;
        await context.Sender.SendAsync(lease.Value).ConfigureAwait(false);
    }
}

/// <summary>Encrypted echo handler: response is AEAD-encrypted (request is sent encrypted by the client).</summary>
[PacketHandler("Bench.EchoEncrypted")]
public static class EchoEncryptedHandlers
{
    [PacketOpcode(0x7301)]
    [PacketEncryption(true)]
    public static async ValueTask EchoAsync(IPacketContext<EchoRequestPacket> context)
    {
        using PacketScope<EchoResponsePacket> lease = PacketFactory<EchoResponsePacket>.Acquire();
        lease.Value.Data = context.Packet.Data;
        await context.Sender.SendAsync(lease.Value, forceEncrypt: true).ConfigureAwait(false);
    }
}
