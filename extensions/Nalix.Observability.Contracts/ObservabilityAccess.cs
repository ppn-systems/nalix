// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Observability.Contracts;

/// <summary>
/// Represents a packet used to request and grant observability access.
/// </summary>
/// <remarks>
/// A request carries an administrative access key. A response carries either the granted
/// <see cref="PermissionLevel"/> or a <see cref="ProtocolReason"/> that explains why access was denied.
/// </remarks>
[Packet]
[ExcludeFromCodeCoverage]
[GenerateFormatterAttribute]
[SerializePackable(SerializeLayout.Explicit)]
[DebuggerDisplay("OBSERVABILITY_ACCESS Stage={Stage}, Granted={AccessLevel}, Reason={Reason}")]
public sealed partial class ObservabilityAccess : PacketBase<ObservabilityAccess>, IPacketValidatable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.OBSERVABILITY_ACCESS;

    /// <summary>
    /// Gets or sets the access packet lifecycle stage.
    /// </summary>
    [SerializeOrder(0)]
    public ObservabilityAccessStage Stage { get; set; }

    /// <summary>
    /// Gets or sets the protocol reason returned by the responder.
    /// </summary>
    [SerializeOrder(1)]
    public ProtocolReason Reason { get; set; }

    /// <summary>
    /// Gets or sets the permission level granted by a successful access response.
    /// </summary>
    [SerializeOrder(2)]
    public PermissionLevel AccessLevel { get; set; }

    /// <summary>
    /// Gets or sets the administrative key supplied by an access request.
    /// </summary>
    [SerializeOrder(3)]
    public Bytes32 AccessKey { get; set; } = Bytes32.Zero;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservabilityAccess"/> class.
    /// </summary>
    public ObservabilityAccess() => this.ResetForPool();

    /// <summary>
    /// Initializes the packet fields for an observability access exchange.
    /// </summary>
    /// <param name="stage">The access lifecycle stage represented by the packet.</param>
    /// <param name="reason">The protocol reason for a response, or <see cref="ProtocolReason.NONE"/> for a request.</param>
    /// <param name="accessLevel">The permission level granted by a successful response.</param>
    /// <param name="accessKey">The administrative access key supplied by a request.</param>
    public void Initialize(
        ObservabilityAccessStage stage,
        ProtocolReason reason = ProtocolReason.NONE,
        PermissionLevel accessLevel = PermissionLevel.NONE,
        Bytes32 accessKey = default)
    {
        this.Priority = PacketPriority.URGENT;
        this.Flags = PacketFlags.SYSTEM;
        this.Stage = stage;
        this.Reason = reason;
        this.AccessLevel = accessLevel;
        this.AccessKey = accessKey;
    }

    /// <summary>
    /// Resets the packet to its pooled default state.
    /// </summary>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Priority = PacketPriority.URGENT;
        this.Flags = PacketFlags.SYSTEM;
        this.Stage = ObservabilityAccessStage.NONE;
        this.Reason = ProtocolReason.NONE;
        this.AccessLevel = PermissionLevel.NONE;
        this.AccessKey = Bytes32.Zero;
    }

    /// <summary>
    /// Validates whether the packet contains the required fields for its current <see cref="Stage"/>.
    /// </summary>
    /// <param name="failureReason">When validation fails, receives a message describing the invalid state.</param>
    /// <returns><see langword="true"/> when the packet is valid; otherwise, <see langword="false"/>.</returns>
    public bool Validate([NotNullWhen(false)] out string? failureReason)
    {
        bool isValid = this.Stage switch
        {
            ObservabilityAccessStage.REQUEST => !this.AccessKey.IsZero,
            ObservabilityAccessStage.RESPONSE => this.Reason != ProtocolReason.NONE || this.AccessLevel > PermissionLevel.NONE,
            ObservabilityAccessStage.NONE or _ => false
        };

        failureReason = isValid ? null : "Invalid fields provided for observability access stage.";
        return isValid;
    }
}

