// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using Nalix.Observability.Contracts;

namespace Nalix.Observability;

/// <summary>
/// Grants supervisor permission to connections that present the administrative observability key.
/// </summary>
[PacketHandler("Nalix.ObservabilityAccess")]
public sealed class ObservabilityAccessHandlers
{
    /// <summary>
    /// Validates the access key and upgrades the connection's permission level when it matches.
    /// </summary>
    /// <param name="context">The packet context containing the request packet and connection state.</param>
    /// <returns>The access response.</returns>
    [PacketEncryption(true)]
    [ReservedOpcodePermitted]
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.OBSERVABILITY_ACCESS)]
    public static ValueTask<ObservabilityAccess> HandleAsync(IPacketContext<ObservabilityAccess> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ObservabilityAccess request = context.Packet;
        if (request.Stage != ObservabilityAccessStage.REQUEST || !request.Validate(out _))
        {
            return RESPOND(ProtocolReason.MALFORMED_PACKET, PermissionLevel.NONE);
        }

        // Bytes32 equality is constant-time, so the comparison leaks no key prefix through timing.
        if (request.AccessKey != ObservabilityKey.Current)
        {
            return RESPOND(ProtocolReason.UNAUTHORIZED, PermissionLevel.NONE);
        }

        context.Connection.Level = PermissionLevel.SUPERVISOR;
        return RESPOND(ProtocolReason.NONE, PermissionLevel.SUPERVISOR);
    }

    private static ValueTask<ObservabilityAccess> RESPOND(ProtocolReason reason, PermissionLevel level)
    {
        ObservabilityAccess response = PacketFactory<ObservabilityAccess>.Acquire().Value;
        response.Initialize(ObservabilityAccessStage.RESPONSE, reason, level);
        return ValueTask.FromResult(response);
    }
}
