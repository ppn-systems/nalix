// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace BlazorWasm.Contracts;

/// <summary>
/// Response to an <see cref="EchoRequestPacket"/>.
/// </summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoResponsePacket : PacketBase<EchoResponsePacket>, IPacketStaticOpcode
{
    /// <summary>The opcode that identifies this packet type on the wire.</summary>
    public static ushort StaticOpCode => 0x7302;

    /// <summary>Gets or sets the echoed text (upper-cased by the server).</summary>
    [SerializeOrder(0)]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the server clock (Unix milliseconds) when the reply was built.</summary>
    [SerializeOrder(1)]
    public long ServerUnixMs { get; set; }
}
