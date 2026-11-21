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
/// Provider to Server proof of possession of the tunnel token. The token itself never crosses the wire.
/// </summary>
[Packet]
[GenerateFormatter]
[ExcludeFromCodeCoverage]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class TunnelProof : PacketBase<TunnelProof>, IFixedSizeSerializable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.TUNNEL_PROOF;

    /// <summary>HMAC-Keccak256(key: token, message: challenge nonce) computed by the provider.</summary>
    [SerializeOrder(0)]
    public Bytes32 Mac { get; set; }

    /// <inheritdoc/>
    public TunnelProof() => this.ResetForPool();

    /// <inheritdoc/>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Priority = PacketPriority.HIGH;
        this.Flags = PacketFlags.NONE;
        this.Mac = default;
    }
}

