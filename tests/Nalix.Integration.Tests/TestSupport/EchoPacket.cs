// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// Variable-size packet used by the end-to-end client/server tests. The server answers with the same packet type
/// carrying <see cref="Sequence"/> + 1 and the upper-cased <see cref="Text"/>, so a reply proves the server ran the
/// handler instead of echoing raw bytes.
/// </summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoPacket : PacketBase<EchoPacket>, IPacketStaticOpcode
{
    public const ushort OpCode = 0x7E01;

    public static ushort StaticOpCode => OpCode;

    [SerializeOrder(0)]
    public int Sequence { get; set; }

    [SerializeOrder(1)]
    public string Text { get; set; } = string.Empty;
}
