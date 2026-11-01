// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Hosting.Tests.Audit;

[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class AuditProbePacket : PacketBase<AuditProbePacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 8901;

    [SerializeOrder(PacketHeaderOffset.Region)]
    public ushort Value { get; set; }
}
