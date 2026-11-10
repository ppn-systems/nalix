// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// Asks the server for <see cref="Count"/> <see cref="EchoPacket"/> replies sent as one batch, numbered
/// 0..Count-1, so the client can check that every frame of the batch arrived, in order.
/// </summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoBatchRequest : PacketBase<EchoBatchRequest>, IPacketStaticOpcode
{
    public const ushort OpCode = 0x7E02;

    public static ushort StaticOpCode => OpCode;

    [SerializeOrder(0)]
    public int Count { get; set; }

    /// <summary>
    /// The text of reply <paramref name="index"/>. Padded so a few hundred replies exceed the 64 KB
    /// a transport coalesces into one write, without the test having to send thousands of packets.
    /// </summary>
    public static string TextFor(int index) => $"item-{index}-{new string('x', 256)}";
}
