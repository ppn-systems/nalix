// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Traversal.Packets;

/// <summary>
/// Server responds to a ReflectorInit with the allocated Reflector Token and the UDP endpoint.
/// </summary>
[Packet]
[GenerateFormatter]
[ExcludeFromCodeCoverage]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class ReflectorAllocated : PacketBase<ReflectorAllocated>, IFixedSizeSerializable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.TRAVERSAL_REFLECTOR_ALLOCATED;

    /// <summary>
    /// The generated Reflector Token that both peers must use to wrap their UDP datagrams.
    /// </summary>
    [SerializeOrder(0)]
    public ulong ReflectorToken { get; set; }

    /// <summary>
    /// Indicates whether the Reflector allocation was successful.
    /// </summary>
    [SerializeOrder(1)]
    public bool Success { get; set; }

    /// <summary>
    /// The other peer of the session this allocation belongs to: the target for the requester's response, and the
    /// requester for the notification sent to the target. Clients must ignore allocations that name another peer.
    /// </summary>
    [SerializeOrder(2)]
    public ulong PeerId { get; set; }

    /// <summary>
    /// The <see cref="ReflectorInit.RequestNonce"/> this allocation answers, or 0 for a notification to the target.
    /// </summary>
    [SerializeOrder(3)]
    public ulong RequestNonce { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReflectorAllocated"/>.
    /// </summary>
    public ReflectorAllocated() => this.ResetForPool();

    /// <inheritdoc/>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Priority = PacketPriority.HIGH;
        this.Flags = PacketFlags.NONE;
        this.ReflectorToken = 0;
        this.Success = false;
        this.PeerId = 0;
        this.RequestNonce = 0;
    }
}

