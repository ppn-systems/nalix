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
/// Server to Provider challenge sent on a new data connection. The nonce is fresh per connection.
/// </summary>
[Packet]
[GenerateFormatter]
[ExcludeFromCodeCoverage]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class TunnelChallenge : PacketBase<TunnelChallenge>, IFixedSizeSerializable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.TUNNEL_CHALLENGE;

    /// <summary>Fresh random nonce the provider must MAC with the tunnel token.</summary>
    [SerializeOrder(0)]
    public Bytes32 Nonce { get; set; }

    /// <inheritdoc/>
    public TunnelChallenge() => this.ResetForPool();

    /// <inheritdoc/>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Priority = PacketPriority.HIGH;
        this.Flags = PacketFlags.NONE;
        this.Nonce = default;
    }
}

