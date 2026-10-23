// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Hosting.Tests.TestSupport;

[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class HandlerScanPacket : PacketBase<HandlerScanPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 8888;

    [SerializeOrder(PacketHeaderOffset.Region)]
    public ushort Value { get; set; }
}
