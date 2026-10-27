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

namespace Nalix.Observability;

/// <summary>
/// Serves runtime observation snapshots: every requested subsystem is sampled into one JSON payload.
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
}
