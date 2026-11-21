// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace UdpDatagram.Contracts;

/// <summary>
/// The reply to an <see cref="EchoRequestPacket"/>; carries the request sequence plus one.
/// <para>
/// Uses opcode <c>0x7302</c>, which is in the user-defined sample range
/// and does not conflict with built-in protocol opcodes.
/// </para>
/// </summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoResponsePacket
    : PacketBase<EchoResponsePacket>,
      IFixedSizeSerializable,
      IPacketStaticOpcode
{
    /// <summary>
    /// The opcode that identifies this packet type on the wire.
    /// </summary>
    public static ushort StaticOpCode => 0x7302;

    /// <summary>
    /// Gets or sets the sequence number chosen by the sender.
    /// </summary>
    [SerializeOrder(0)]
    public int Sequence { get; set; }
}
