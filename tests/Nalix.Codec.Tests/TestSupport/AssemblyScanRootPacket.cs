// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Codec.Tests.TestSupport;

/// <summary>
/// Test-only packet for namespace scanning.
/// </summary>
[GenerateFormatter]
public sealed partial class AssemblyScanRootPacket : PacketBase<AssemblyScanRootPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    [SerializeOrder(PacketHeaderOffset.Region)]
    public ushort Value { get; set; }

    public static new AssemblyScanRootPacket Deserialize(ReadOnlySpan<byte> buffer)
        => PacketBase<AssemblyScanRootPacket>.Deserialize(buffer);
}
