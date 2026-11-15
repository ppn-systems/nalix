// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// Packet whose handler requires a permission level no ordinary client holds, so every request is denied by the
/// dispatcher before the handler runs.
/// </summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoAdminPacket : PacketBase<EchoAdminPacket>, IPacketStaticOpcode
{
    public const ushort OpCode = 0x7E03;

    public static ushort StaticOpCode => OpCode;

    [SerializeOrder(0)]
    public int Sequence { get; set; }
}
