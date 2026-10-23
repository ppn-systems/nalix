// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Network.Tests.TestSupport;

[Packet]
[GenerateFormatter]
public sealed partial class HostingScanAttributedPacket : PacketBase<HostingScanAttributedPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;
    [SerializeOrder(PacketHeaderOffset.Region)]
    public ushort Value { get; set; }
}
