// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace BlazorWasm.Contracts;

/// <summary>
/// A chat line. Clients send it; the server broadcasts it to every connected client (server push).
/// </summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class ChatMessagePacket : PacketBase<ChatMessagePacket>, IPacketStaticOpcode
{
    /// <summary>The opcode that identifies this packet type on the wire.</summary>
    public static ushort StaticOpCode => 0x7303;

    /// <summary>Gets or sets the display name of the sender.</summary>
    [SerializeOrder(0)]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the message text.</summary>
    [SerializeOrder(1)]
    public string Message { get; set; } = string.Empty;
}
