// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Tunneling;

/// <summary>
/// Packet sent from Provider to Server on a new data connection to start authenticating it.
/// Carries the public tunnel identifier <c>Keccak256(token)</c>, never the token itself.
/// The server answers with <c>TUNNEL_CHALLENGE</c>.
/// </summary>
[Packet]
[GenerateFormatter]
[ExcludeFromCodeCoverage]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class TunnelReady : PacketBase<TunnelReady>, IFixedSizeSerializable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.TUNNEL_READY;

    /// <summary>Tunnel identifier: Keccak256 of the one-time tunnel token.</summary>
    [SerializeOrder(0)]
    public Bytes32 Token { get; set; }

    /// <inheritdoc/>
    public TunnelReady() => this.ResetForPool();

    /// <inheritdoc/>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Priority = PacketPriority.HIGH;
        this.Flags = PacketFlags.NONE;
        this.Token = default;
    }
}

