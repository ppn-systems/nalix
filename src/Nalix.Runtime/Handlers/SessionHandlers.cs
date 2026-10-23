// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security.Hashing;
using Nalix.Environment.Time;
using Nalix.Runtime.Extensions;

namespace Nalix.Runtime.Handlers;

/// <summary>
/// Handles dedicated session resume packets.
/// </summary>
[PacketHandler("Nalix.Session")]
public static partial class SessionHandlers
{
    [Inject]
    private static ISessionService s_sessionService = null!;

    /// <summary>
    /// Handles a session resume request and restores the connection state when the token is valid.
    /// </summary>
    /// <param name="context">The typed packet context for the incoming session signal.</param>
    /// <returns>The acknowledgement signal when the resume succeeds; otherwise <see langword="null"/> after disconnecting.</returns>
    [ReservedOpcodePermitted]
    [PacketEncryption(false)]
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.SESSION_SIGNAL)]
    public static async ValueTask HandleAsync(IPacketContext<SessionResume> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsReliable)
        {
            // This is a replayed packet, ignore silently.
            return;
        }

        IConnection connection = context.Connection;
        IConnectionHub? hub = connection.GetHub();

        if (hub is null)
        {
            await HandleFailureAsync(context, ProtocolReason.SERVICE_UNAVAILABLE).ConfigureAwait(false);
            return;
        }

        if (connection.GetRuntimeState().HandshakeEstablished)
        {
            await HandleFailureAsync(context, ProtocolReason.STATE_VIOLATION).ConfigureAwait(false);
            return;
        }

        SessionResume packet = context.Packet;

        if (!packet.Validate(out string? reason))
        {
            await HandleFailureAsync(context, ProtocolReason.MALFORMED_PACKET).ConfigureAwait(false);
            return;
        }

        if (packet.Stage != SessionResumeStage.REQUEST)
        {
            return;
        }

        // SEC-33: Atomically consume the token only after SEC-16 proof validation succeeds.
        // Two parallel valid requests with the same token: only one predicate-accepted remove wins.
        // Invalid proofs must not remove the entry, otherwise an observer could burn a resume token.
        using SessionScope scope = await s_sessionService.ConsumeAsync(
            packet.SessionToken,
            session => IsResumeProofValid(session, packet.SessionToken, packet.Proof)).ConfigureAwait(false);
        SessionEntry? session = scope.Value;
        if (session == null)
        {
            await HandleFailureAsync(context, ProtocolReason.SESSION_EXPIRED).ConfigureAwait(false);
            return;
        }

        // Token was already consumed atomically by ConsumeAsync — no separate RemoveAsync needed.
        RestoreSessionSnapshot(connection, session);
        connection.GetRuntimeState().HandshakeEstablished = true;

        ConnectionSequenceState seqState = connection.GetSequenceState();

        // Reset sequence counters for the newly established transport connection so both client
        // and server start from sequence 0 on the resumed transport.
        seqState.TcpSendSequence = 0;
        seqState.TcpReceiveSequence = 0;
        connection.TCP.SendSequence.Reset();
        connection.TCP.ReceiveSequence.Reset();

        if (connection.IsUdpCreated && connection.UDP is not null)
        {
            seqState.UdpSendSequence = 0;
            seqState.UdpReceiveSequence = 0;
            connection.UDP.SendSequence.Reset();
            connection.UDP.ReceiveSequence.Reset();
        }

        await s_sessionService.SaveSessionAsync(connection).ConfigureAwait(false);

        ulong newToken = connection.ConnectionId;

        Span<byte> responseMessageBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(responseMessageBytes, newToken);
        BinaryPrimitives.WriteInt64LittleEndian(responseMessageBytes[8..], Clock.UnixSecondsNow() / 30);
        Span<byte> responseProofBytes = stackalloc byte[32];
        HmacKeccak256.Compute(session.Snapshot.Secret.AsSpan(), responseMessageBytes, responseProofBytes);

        using PacketScope<SessionResume> lease = PacketFactory<SessionResume>.Acquire();
        SessionResume ack = lease.Value;
        ack.Initialize(
            stage: SessionResumeStage.RESPONSE,
            sessionToken: newToken,
            reason: ProtocolReason.NONE,
            proof: new Bytes32(responseProofBytes),
            flags: packet.Flags);

        await context.Sender.SendAsync(ack).ConfigureAwait(false);
    }

    private static bool IsResumeProofValid(SessionEntry session, ulong sessionToken, Bytes32 proof)
    {
        if (session.Snapshot.Secret.IsZero)
        {
            return false;
        }

        Span<byte> messageBytes = stackalloc byte[16];
        Span<byte> expectedProofBytes = stackalloc byte[32];

        BinaryPrimitives.WriteUInt64LittleEndian(messageBytes, sessionToken);

        long currentWindow = Clock.UnixSecondsNow() / 30;

        // SEC-16: Validate proof-of-possession (MAC) using the stored session secret and sliding window.
        // We compute HMAC-Keccak256(Secret, SessionToken || TimeWindow) for t-1, t, t+1.
        for (long w = currentWindow - 1; w <= currentWindow + 1; w++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(messageBytes[8..], w);
            HmacKeccak256.Compute(session.Snapshot.Secret.AsSpan(), messageBytes, expectedProofBytes);

            if (proof == new Bytes32(expectedProofBytes))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Restores the saved session snapshot onto the live connection before acknowledging resume.
    /// </summary>
    /// <param name="connection">The connection being resumed.</param>
    /// <param name="session">The stored session entry.</param>
    private static void RestoreSessionSnapshot(IConnection connection, SessionEntry session)
    {
        SessionSnapshot snapshot = session.Snapshot;

        connection.Level = snapshot.Level;
        connection.Secret = snapshot.Secret;
        connection.Algorithm = snapshot.Algorithm;

        if (snapshot.Attributes is not null)
        {
            foreach (KeyValuePair<AttributeKey, object> attribute in snapshot.Attributes)
            {
                connection.Attributes[attribute.Key] = attribute.Value;
            }
        }

        connection.GetRuntimeState().HandshakeEstablished = true;
    }

    /// <summary>
    /// Sends a failure acknowledgement and disconnects the connection.
    /// </summary>
    /// <param name="context">The connection to close.</param>
    /// <param name="reason">The failure reason to report.</param>
    private static async ValueTask HandleFailureAsync(IPacketContext<SessionResume> context, ProtocolReason reason)
    {
        using PacketScope<SessionResume> lease = PacketFactory<SessionResume>.Acquire();
        SessionResume ack = lease.Value;
        ack.Initialize(
            stage: SessionResumeStage.RESPONSE,
            sessionToken: default,
            reason: reason,
            flags: PacketFlags.SYSTEM);

        try
        {
            await context.Sender.SendAsync(ack).ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);
        }
        finally
        {
            context.Connection.Disconnect($"Session resume rejected: {reason}");
        }
    }
}
