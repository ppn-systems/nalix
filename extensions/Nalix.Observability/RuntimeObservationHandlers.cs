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
    /// Samples the requested subsystems and returns them in a single response.
    /// </summary>
    /// <param name="context">The packet context.</param>
    /// <returns>The observation response.</returns>
    [PacketEncryption(true)]
    [ReservedOpcodePermitted]
    [PacketOpcode(ProtocolOpCode.RUNTIME_OBSERVATION)]
    [PacketPermission(PermissionLevel.SUPERVISOR)]
    public static ValueTask<RuntimeObservation> HandleAsync(IPacketContext<RuntimeObservation> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RuntimeObservation request = context.Packet;
        RuntimeObservation response = PacketFactory<RuntimeObservation>.Acquire().Value;

        if (request.Stage == RuntimeObservationStage.HISTORY_REQUEST)
        {
            return ValueTask.FromResult(HISTORY(request, response));
        }

        if (request.Stage != RuntimeObservationStage.REQUEST || !request.Validate(out _))
        {
            response.Initialize(RuntimeObservationStage.RESPONSE, request.Targets, ProtocolReason.MALFORMED_PACKET);
            return ValueTask.FromResult(response);
        }

        long sampledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        BufferLease payload = ObservationWriter.Write(request.Targets, out RuntimeObservationTargets written);

        if (written == RuntimeObservationTargets.NONE)
        {
            payload.Dispose();
            response.Initialize(RuntimeObservationStage.RESPONSE, request.Targets, ProtocolReason.NOT_FOUND, sampledAt);
            return ValueTask.FromResult(response);
        }

        response.Initialize(RuntimeObservationStage.RESPONSE, written, ProtocolReason.NONE, sampledAt);
        response.AssociateLease(payload);
        return ValueTask.FromResult(response);
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
            ? HistoryQuery.Catalog(recorder, from, to)
            : HistoryQuery.Series(recorder, from, to, points, metrics);

        response.Initialize(RuntimeObservationStage.HISTORY_RESPONSE, RuntimeObservationTargets.NONE, ProtocolReason.NONE, now);
        response.AssociateLease(payload);
        return response;
    }
}
