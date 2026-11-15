// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Traversal.Packets;

/// <summary>
/// Client requests the server to allocate a Reflector Session for communicating with TargetPeerId.
/// </summary>
[Packet]
[GenerateFormatter]
[ExcludeFromCodeCoverage]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class ReflectorInit : PacketBase<ReflectorInit>, IFixedSizeSerializable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.TRAVERSAL_REFLECTOR_INIT;

    /// <summary>
    /// The ID of the peer we want to Reflector data to.
    /// </summary>
    [SerializeOrder(0)]
    public ulong TargetPeerId { get; set; }

    /// <summary>
    /// Client-chosen, non-zero correlation nonce. The server echoes it in <see cref="ReflectorAllocated.RequestNonce"/>
    /// so the requester can tell its own allocation apart from any other allocation it receives.
    /// </summary>
    [SerializeOrder(1)]
    public ulong RequestNonce { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReflectorInit"/>.
    /// </summary>
    public ReflectorInit() => this.ResetForPool();

    /// <inheritdoc/>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Priority = PacketPriority.HIGH;
        this.Flags = PacketFlags.NONE;
        this.TargetPeerId = 0;
        this.RequestNonce = 0;
    }
}

