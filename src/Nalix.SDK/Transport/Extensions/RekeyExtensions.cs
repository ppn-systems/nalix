// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
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

        TransportSession? transport = session as TransportSession;
        bool switched = false;

        void SWITCH_KEY()
        {
            // Switch to the new key and reset the sequence counters so that every later frame uses the new state.
            session.State.Secret = newKey;
            session.ResetSequenceCounters();
            switched = true;
        }

        SdkLog.Debug(Tag, "Rotating the session key.");

        try
        {
            // Send SESSION_REKEY and await SESSION_REKEY_ACK. On TCP/WebSocket the key switch runs under the send lock
            // right after the request is written, so no frame encrypted with the old key can follow it on the wire.
            using Control ack = await PacketAwaiter.AwaitAsync<Control>(
                session,
                predicate: p => p.Type == ControlType.SESSION_REKEY_ACK && p.SequenceId == seqId,
                sendAsync: async token =>
                {
                    if (transport is not null)
                    {
                        await transport.SendThenSwitchAsync(rekeyPacket, SWITCH_KEY, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await session.SendAsync(rekeyPacket, token).ConfigureAwait(false);
                        SWITCH_KEY();
                    }
                },
                timeoutMs: session.Options.ConnectTimeoutMillis > 0 ? session.Options.ConnectTimeoutMillis : 5000,
                ct: ct).ConfigureAwait(false);

            SdkLog.Info(Tag, "Session key rotated.");
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (!switched)
            {
                // SESSION_REKEY never left: the server still uses the current key and nothing changed locally.
                SdkLog.Warn(Tag, "Key rotation failed before the request was sent; the session keeps its current key.", ex);
            }
            else if (transport is { DeliversRekeyReliably: true })
            {
                // The request was written on an ordered transport, and the server switches as soon as it reads it. The
                // acknowledgement is only late or lost: going back to the old key would leave the two sides on different
                // keys, so the new key is kept.
                SdkLog.Warn(Tag, "No acknowledgement for the key rotation; keeping the new key, which the server switched to when it read the request.", ex);
            }
            else
            {
                // Unordered transport: the request may never have arrived, and the server ignores it on UDP.
                session.State.Secret = previousKey;
                session.ResetSequenceCounters();
                SdkLog.Warn(Tag, "Key rotation was not acknowledged on an unordered transport; the previous key was restored.", ex);
            }

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

    private const string Tag = "SDK.Rekey";
}
