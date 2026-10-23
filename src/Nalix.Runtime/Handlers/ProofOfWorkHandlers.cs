// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;

namespace Nalix.Runtime.Handlers;

/// <summary>
/// Provides handlers for Proof-of-Work negotiation and verification.
/// </summary>
[PacketHandler("Nalix.ProofOfWork")]
public static class ProofOfWorkHandlers
{
    /// <summary>
    /// Handles the incoming POW_PROOF packet containing the client's solution.
    /// </summary>
    [ReservedOpcodePermitted]
    [PacketEncryption(false)]
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.POW_PROOF)]
    public static void HandleAsync(IPacketContext<ProofOfWorkProof> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsReliable)
        {
            // This is a replayed packet, ignore silently.
            return;
        }

        if (context.Connection.Level >= PermissionLevel.POW_VERIFIED)
        {
            // Already verified, ignore silently without re-running PoW or disconnecting.
            return;
        }

        ProofOfWorkProof p = context.Packet;
        long currentTime = System.Environment.TickCount64;

        // Validate difficulty and timestamp bounds safely without signed subtraction overflow
        if (p.Difficulty == 0 ||
            p.Difficulty > 32 ||
            p.TimestampTicks <= 0 ||
            currentTime < p.TimestampTicks - 5000 ||
            currentTime - p.TimestampTicks > 30000)
        {
            context.Connection.Disconnect(ProtocolReason.POW_INVALID.ToString());
            return;
        }

        Bytes32 mac = p.Mac;
        Bytes32 nonce = p.Nonce;

        if (!ProofOfWork.VerifySolution(nonce.AsSpan(), p.Difficulty, p.TimestampTicks, context.Connection.ConnectionId, mac.AsSpan(), p.Solution))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog(
                        "RT.PowHandlers:HandlePowProofAsync",
                        $"PoW rejected for {context.Connection.NetworkEndpoint} (Diff={p.Difficulty})"));
            }

            context.Connection.Disconnect(ProtocolReason.POW_INVALID.ToString());
            return;
        }

        if (context.Connection.Level < PermissionLevel.POW_VERIFIED)
        {
            // Already verified, ignore silently.
            context.Connection.Level = PermissionLevel.POW_VERIFIED;
        }
    }
}
