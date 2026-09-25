// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace BlazorWasm.Contracts;

/// <summary>
/// Request sent by the browser; the server answers with an <see cref="EchoResponsePacket"/>.
/// </summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoRequestPacket : PacketBase<EchoRequestPacket>, IPacketStaticOpcode
{
    /// <summary>The opcode that identifies this packet type on the wire.</summary>
    public static ushort StaticOpCode => 0x7301;

    /// <summary>Gets or sets the text to echo.</summary>
    [SerializeOrder(0)]
    public string Text { get; set; } = string.Empty;
}
