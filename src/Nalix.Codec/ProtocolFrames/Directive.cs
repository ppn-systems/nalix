// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Codec.ProtocolFrames;

/// <summary>
/// Represents a directive frame used for control and server feedback.
/// </summary>
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
[DebuggerDisplay("Directive Seq={SequenceId}, Type={Type}, Reason={Reason}, Action={Action}")]
public sealed partial class Directive : PacketBase<Directive>, IPacketReasoned, IFixedSizeSerializable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.SYSTEM_DIRECTIVE;

    /// <summary>
    /// Gets or sets the directive type.
    /// </summary>
    [SerializeOrder(0)]
    public ControlType Type { get; set; }

    /// <summary>
    /// Gets or sets the reason for the directive.
    /// </summary>
    [SerializeOrder(1)]
    public ProtocolReason Reason { get; set; }

    /// <summary>
    /// Gets or sets the suggested action for the client.
    /// </summary>
    [SerializeOrder(2)]
    public ProtocolAdvice Action { get; set; }

    /// <summary>
    /// Gets or sets directive flags.
    /// </summary>
    [SerializeOrder(3)]
    public ControlFlags Control { get; set; }

    /// <summary>
    /// Gets or sets the first directive argument.
    /// Usage: RetryAfterMs (for RATE_LIMITED), AlgorithmId (for CIPHER_UPDATE).
    /// </summary>
    [SerializeOrder(4)]
    public uint Arg0 { get; set; }

    /// <summary>
    /// Gets or sets the second directive argument.
    /// Usage: Credit (for RATE_LIMITED), SequenceId (for ACK/NACK).
    /// </summary>
    [SerializeOrder(5)]
    public uint Arg1 { get; set; }

    /// <summary>
    /// Gets or sets the third directive argument.
    /// Usage: OpCode (for FAIL/ERROR), Port (for REDIRECT).
    /// </summary>
    [SerializeOrder(6)]
    public ushort Arg2 { get; set; }

    /// <summary>
    /// Initializes a new instance with default values.
    /// </summary>
    public Directive() => this.ResetForPool();

    /// <summary>Initializes the directive payload.</summary>
    /// <param name="type">The directive type.</param>
    /// <param name="reason">The reason code.</param>
    /// <param name="action">The suggested client action.</param>
    /// <param name="sequenceId">The sequence identifier.</param>
    /// <param name="flags">The directive flags.</param>
    /// <param name="arg0">The first directive argument.</param>
    /// <param name="arg1">The second directive argument.</param>
    /// <param name="arg2">The third directive argument.</param>
    /// <param name="controlFlags">The system-level control flags.</param>
    public void Initialize(
        ControlType type, ProtocolReason reason, ProtocolAdvice action,
        ushort sequenceId, PacketFlags flags = PacketFlags.SYSTEM,
        ControlFlags controlFlags = ControlFlags.NONE,
        uint arg0 = 0, uint arg1 = 0, ushort arg2 = 0)
    {
        this.Arg0 = arg0;
        this.Arg1 = arg1;
        this.Arg2 = arg2;
        this.Type = type;
        this.Flags = flags;
        this.Reason = reason;
        this.Action = action;
        this.Control = controlFlags;
        this.SequenceId = sequenceId;


        this.Priority = PacketPriority.HIGH;
    }



    /// <inheritdoc/>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Type = ControlType.NONE;
        this.Reason = ProtocolReason.NONE;
        this.Action = ProtocolAdvice.NONE;
        this.Control = ControlFlags.NONE;
        this.Arg0 = 0;
        this.Arg1 = 0;
        this.Arg2 = 0;
        this.Priority = PacketPriority.HIGH;
        this.Flags = PacketFlags.SYSTEM;
    }

    /// <summary>
    /// Returns a string representation of the directive and all its fields.
    /// </summary>
    /// <returns>String describing the Directive packet.</returns>
    public override string ToString()
        => $"Directive [SequenceId={this.SequenceId}, Type={this.Type}, Reason={this.Reason}, Action={this.Action}, Control={this.Control}, Arg0={this.Arg0}, Arg1={this.Arg1}, Arg2={this.Arg2}, OpCode={this.OpCode}, Priority={this.Priority}]";
}
