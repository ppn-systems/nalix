// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using Nalix.Environment.Memory;
using Nalix.Observability.Contracts;
using Nalix.Observability.Internal;
using Nalix.Observability.Internal.History;

namespace Nalix.Observability;

/// <summary>
/// Serves runtime observation snapshots (every requested subsystem sampled into one JSON payload) and
/// recorded-history queries.
/// </summary>
[PacketHandler("Nalix.RuntimeObservation")]
public sealed class RuntimeObservationHandlers
{
    /// <summary>
    /// Samples the requested subsystems and streams them back in chunks that each fit the payload budget.
    /// </summary>
    /// <remarks>
    /// Every chunk carries the request's sequence id and the sections it holds in
    /// <see cref="RuntimeObservation.Targets"/>; the runtime flags the last one with
    /// <see cref="RuntimeObservation.IsEndOfStream"/>. A snapshot that fits the budget is a single chunk, and history
    /// queries are always one chunk, so <c>RequestAsync</c> callers keep working.
    /// </remarks>
    /// <param name="context">The packet context.</param>
    /// <returns>The response chunks.</returns>
    [PacketEncryption(true)]
    [ReservedOpcodePermitted]
    [PacketOpcode(ProtocolOpCode.RUNTIME_OBSERVATION)]
    [PacketPermission(PermissionLevel.SUPERVISOR)]
    public static async IAsyncEnumerable<RuntimeObservation> HandleAsync(IPacketContext<RuntimeObservation> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Read the request before the first yield: the iterator body runs lazily.
        RuntimeObservation request = context.Packet;
        ushort sequenceId = request.SequenceId;
        RuntimeObservationStage stage = request.Stage;
        RuntimeObservationTargets targets = request.Targets;
        bool valid = request.Validate(out _);

        if (stage == RuntimeObservationStage.HISTORY_REQUEST)
        {
            yield return STAMP(HISTORY(request, PacketFactory<RuntimeObservation>.Acquire().Value), sequenceId);
            yield break;
        }

        if (stage != RuntimeObservationStage.REQUEST || !valid)
        {
            RuntimeObservation rejected = PacketFactory<RuntimeObservation>.Acquire().Value;
            rejected.Initialize(RuntimeObservationStage.RESPONSE, targets, ProtocolReason.MALFORMED_PACKET);
            yield return STAMP(rejected, sequenceId);
            yield break;
        }

        long sampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<(BufferLease Payload, RuntimeObservationTargets Written)> chunks = ObservationWriter.WriteChunks(targets, ObservabilityLimits.PayloadBudget);

        for (int i = 0; i < chunks.Count; i++)
        {
            (BufferLease payload, RuntimeObservationTargets written) = chunks[i];
            RuntimeObservation response = PacketFactory<RuntimeObservation>.Acquire().Value;
            if (written == RuntimeObservationTargets.NONE)
            {
                payload.Dispose();
                response.Initialize(RuntimeObservationStage.RESPONSE, targets, ProtocolReason.NOT_FOUND, sampledAt);
            }
            else
            {
                response.Initialize(RuntimeObservationStage.RESPONSE, written, ProtocolReason.NONE, sampledAt);
                response.AssociateLease(payload);
            }

            yield return STAMP(response, sequenceId);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static RuntimeObservation STAMP(RuntimeObservation response, ushort sequenceId)
    {
        response.SequenceId = sequenceId;
        return response;
    }

    // Recorded history: an empty metric list returns the catalog, otherwise the bucketed series.
    private static RuntimeObservation HISTORY(RuntimeObservation request, RuntimeObservation response)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HistoryRecorder? recorder = HistoryRecorder.Current;
        if (recorder is null)
        {
            response.Initialize(RuntimeObservationStage.HISTORY_RESPONSE, RuntimeObservationTargets.NONE, ProtocolReason.OPERATION_UNSUPPORTED, now);
            return response;
        }

        if (!request.Validate(out _) ||
            !HistoryQuery.TryParse(request.ObservationData.Span, out long from, out long to, out int points, out List<string> metrics))
        {
            response.Initialize(RuntimeObservationStage.HISTORY_RESPONSE, RuntimeObservationTargets.NONE, ProtocolReason.MALFORMED_PACKET, now);
            return response;
        }

        BufferLease payload = metrics.Count == 0
            ? HistoryQuery.Catalog(recorder, from, to, ObservabilityLimits.PayloadBudget)
            : HistoryQuery.Series(recorder, from, to, HistoryQuery.FitPoints(points, metrics.Count, ObservabilityLimits.PayloadBudget), metrics);

        response.Initialize(RuntimeObservationStage.HISTORY_RESPONSE, RuntimeObservationTargets.NONE, ProtocolReason.NONE, now);
        response.AssociateLease(payload);
        return response;
    }
}
