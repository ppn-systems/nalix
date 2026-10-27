// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;

namespace Nalix.Observability.Contracts;

/// <summary>
/// Represents a packet used to request and return runtime observation data.
/// </summary>
/// <remarks>
/// A request selects one or more <see cref="RuntimeObservationTargets"/>. A response echoes the targets it
/// actually wrote and carries a single UTF-8 JSON object keyed by <see cref="RuntimeObservationSections"/>,
/// or a <see cref="ProtocolReason"/> when the request cannot be served.
/// </remarks>
[Packet]
[ExcludeFromCodeCoverage]
[GenerateFormatterAttribute]
[SerializePackable(SerializeLayout.Explicit)]
[DebuggerDisplay("RUNTIME_OBSERVATION Stage={Stage}, Targets={Targets}, Reason={Reason}")]
public sealed partial class RuntimeObservation : PacketBase<RuntimeObservation>, IPacketValidatable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.RUNTIME_OBSERVATION;

    /// <summary>
    /// Gets or sets the observation packet lifecycle stage.
    /// </summary>
    [SerializeOrder(0)]
    public RuntimeObservationStage Stage { get; set; }

    /// <summary>
    /// Gets or sets the requested subsystems, or on a response the subsystems present in the payload.
    /// </summary>
    [SerializeOrder(1)]
    public RuntimeObservationTargets Targets { get; set; }

    /// <summary>
    /// Gets or sets the protocol reason returned by the responder.
    /// </summary>
    [SerializeOrder(2)]
    public ProtocolReason Reason { get; set; }

    /// <summary>
    /// Gets or sets the server wall-clock time, in Unix milliseconds, at which the payload was sampled.
    /// </summary>
    [SerializeOrder(3)]
    public long SampledAt { get; set; }

    /// <summary>
    /// Gets or sets the UTF-8 JSON observation payload returned by a response.
    /// </summary>
    [SerializeOrder(4)]
    public ReadOnlyMemory<byte> ObservationData { get; set; } = ReadOnlyMemory<byte>.Empty;

    [SerializeIgnore]
    private BufferLease? _lease;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeObservation"/> class.
    /// </summary>
    public RuntimeObservation() => this.ResetForPool();

    /// <summary>
    /// Associates a rented buffer lease with the packet and transfers ownership to the packet.
    /// </summary>
    /// <remarks>
    /// The lease is disposed when the packet is reset, which returns the underlying buffer to the pool.
    /// </remarks>
    /// <param name="lease">The rented buffer lease to associate.</param>
    public void AssociateLease(BufferLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _lease?.Dispose();
        _lease = lease;
        this.ObservationData = lease.Memory;
    }

    /// <summary>
    /// Initializes the packet fields for a runtime observation exchange.
    /// </summary>
    /// <param name="stage">The observation lifecycle stage represented by the packet.</param>
    /// <param name="targets">The subsystems requested, or written by a response.</param>
    /// <param name="reason">The protocol reason for a response, or <see cref="ProtocolReason.NONE"/> for a request.</param>
    /// <param name="sampledAt">The server sampling time in Unix milliseconds; zero for a request.</param>
    /// <param name="observationData">The JSON observation payload supplied by a response.</param>
    public void Initialize(
        RuntimeObservationStage stage,
        RuntimeObservationTargets targets,
        ProtocolReason reason = ProtocolReason.NONE,
        long sampledAt = 0,
        ReadOnlyMemory<byte> observationData = default)
    {
        this.Priority = PacketPriority.HIGH;
        this.Flags = PacketFlags.SYSTEM;
        this.Stage = stage;
        this.Targets = targets;
        this.Reason = reason;
        this.SampledAt = sampledAt;
        this.ObservationData = observationData;
    }

    /// <summary>
    /// Resets the packet to its pooled default state and releases any associated buffer lease.
    /// </summary>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Priority = PacketPriority.HIGH;
        this.Flags = PacketFlags.SYSTEM;
        this.Stage = RuntimeObservationStage.NONE;
        this.Targets = RuntimeObservationTargets.NONE;
        this.Reason = ProtocolReason.NONE;
        this.SampledAt = 0;
        this.ObservationData = ReadOnlyMemory<byte>.Empty;
        _lease?.Dispose();
        _lease = null;
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
            RuntimeObservationStage.REQUEST => this.Targets != RuntimeObservationTargets.NONE,
            RuntimeObservationStage.HISTORY_REQUEST => !this.ObservationData.IsEmpty,
            RuntimeObservationStage.RESPONSE or RuntimeObservationStage.HISTORY_RESPONSE => this.Reason != ProtocolReason.NONE || !this.ObservationData.IsEmpty,
            RuntimeObservationStage.NONE or _ => false
        };

        failureReason = isValid ? null : "Invalid fields provided for runtime observation stage.";
        return isValid;
    }
}
