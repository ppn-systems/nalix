// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Codec.Tests.TestSupport.AssemblyScanChild;

/// <summary>
/// Test-only child namespace packet for recursive scanning.
/// </summary>
[GenerateFormatter]
public sealed partial class AssemblyScanChildPacket : PacketBase<AssemblyScanChildPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    [SerializeOrder(PacketHeaderOffset.Region)]
    public ushort Value { get; set; }

    public static new AssemblyScanChildPacket Deserialize(ReadOnlySpan<byte> buffer)
        => PacketBase<AssemblyScanChildPacket>.Deserialize(buffer);
}
