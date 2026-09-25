// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Environment.Random;
using Nalix.SDK.Transport.Internal;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Provides extension methods for session rekeying (Key Rotation).
/// </summary>
public static class RekeyExtensions
{
    /// <summary>
    /// Performs a mid-session key rotation using HKDF ratcheting.
    /// Sends a <see cref="ControlType.SESSION_REKEY"/> control packet to the server,
    /// and resets the session's sequence counters to prevent overflow (<see cref="Abstractions.Exceptions.CipherException"/>).
    /// </summary>
    /// <param name="session">The transport session.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the key rotation is successfully acknowledged by the server.</returns>
    public static async ValueTask RekeyAsync(this ITransportSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        Bytes32 previousKey = session.State.Secret;
        Bytes32 newKey = HandshakeX25519.DeriveRekeySecret(session.State.Secret);

        ushort seqId = (ushort)Csprng.GetInt32(1, ushort.MaxValue);

        using PacketScope<Control> lease = PacketFactory<Control>.Acquire();
        Control rekeyPacket = lease.Value;
        rekeyPacket.Initialize(ControlType.SESSION_REKEY, seqId, PacketFlags.SYSTEM, ProtocolReason.NONE);

        try
        {
            // Send the SESSION_REKEY packet and await the SESSION_REKEY_ACK from the server.
            // We use AwaitAsync to ensure the server has processed the new key before we switch our local state.
            using Control ack = await PacketAwaiter.AwaitAsync<Control>(
                session,
                predicate: p => p.Type == ControlType.SESSION_REKEY_ACK && p.SequenceId == seqId,
                sendAsync: async token =>
                {
                    await session.SendAsync(rekeyPacket, token).ConfigureAwait(false);

                    // Immediately switch to the new key and reset sequence counters for outbound frames
                    // so that subsequent frames (including any other concurrent sends) use the new state.
                    session.State.Secret = newKey;
                    session.ResetSequenceCounters();
                },
                timeoutMs: session.Options.ConnectTimeoutMillis > 0 ? session.Options.ConnectTimeoutMillis : 5000,
                ct: ct).ConfigureAwait(false);
        }
        catch
        {
            session.State.Secret = previousKey;
            session.ResetSequenceCounters();
            throw;
        }
        finally
        {
            // Both generations of the session key were copied into this async method's state
            // machine, which lives on the GC heap. The live key is in session.State.Secret; these
            // copies are not needed once the rotation has settled either way.
            Bytes32.Wipe(ref previousKey);
            Bytes32.Wipe(ref newKey);
        }
    }
}
