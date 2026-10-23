// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security.Hashing;
using Nalix.Environment.Time;
using Nalix.SDK.Transport.Internal;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Provides reconnect and session resume helpers for <see cref="TcpSession"/>.
/// </summary>
public static class ResumeExtensions
{
    /// <summary>
    /// Attempts to resume the existing session state on an already connected TCP session.
    /// </summary>
    /// <param name="session">The connected TCP session to resume.</param>
    /// <param name="ct">The cancellation token to observe.</param>
    /// <returns>The <see cref="ProtocolReason"/> reported by the server. <see cref="ProtocolReason.NONE"/> indicates success.</returns>
    public static async ValueTask<ProtocolReason> ResumeSessionAsync(this ITransportSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.IsConnected)
        {
            throw new InvalidOperationException("Session must be connected to perform resume.");
        }

        if (!HasResumeState(session.State))
        {
            return ProtocolReason.SESSION_NOT_FOUND;
        }

        using SessionResume request = new();
        // The token is assigned to the session during the initial Handshake (SERVER_FINISH).
        request.Initialize(SessionResumeStage.REQUEST, session.State.SessionToken);

        // SEC-16: Compute proof-of-possession using HMAC-Keccak256(Secret, SessionToken || TimeWindow).
        // This proves to the server that we own the session secret and prevents replays via a sliding time window.
        Span<byte> proofBytes = stackalloc byte[32];
        Span<byte> messageBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(messageBytes, session.State.SessionToken);
        BinaryPrimitives.WriteInt64LittleEndian(messageBytes[8..], Clock.UnixSecondsNow() / 30);

        // SEC-16: Use fast HMAC instead of slow PBKDF2 for session resumption.
        HmacKeccak256.Compute(session.State.Secret.AsSpan(), messageBytes, proofBytes);
        request.Proof = new Bytes32(proofBytes);

        TransportSession? transportSession = session as TransportSession;
        transportSession?.EnterNegotiation();

        try
        {
            SessionResume response = await PacketAwaiter.AwaitAsync<SessionResume>(
                session,
                predicate: packet => packet.Stage == SessionResumeStage.RESPONSE,
                timeoutMs: session.Options.ResumeTimeoutMillis,
                sendAsync: token => session.SendAsync(request, encrypt: false, token),
                ct).ConfigureAwait(false);

            if (!response.Validate(out string? validationReason))
            {
                throw new NetworkException($"Malformed SessionResume response packet: {validationReason}");
            }

            if (response.Reason != ProtocolReason.NONE)
            {
                return response.Reason;
            }

            Span<byte> responseMessageBytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(responseMessageBytes, response.SessionToken);
            BinaryPrimitives.WriteInt64LittleEndian(responseMessageBytes[8..], Clock.UnixSecondsNow() / 30);
            Span<byte> expectedResponseProofBytes = stackalloc byte[Bytes32.Size];
            HmacKeccak256.Compute(session.State.Secret.AsSpan(), responseMessageBytes, expectedResponseProofBytes);

            if (response.Proof != new Bytes32(expectedResponseProofBytes))
            {
                throw new NetworkException("Session resume response proof is invalid. Possible Man-in-the-Middle attack or clock skew.");
            }

            session.State.SessionToken = response.SessionToken;
            session.State.EncryptionEnabled = true;
            transportSession?.NotifyReady();
            return ProtocolReason.NONE;
        }
        catch (OperationCanceledException)
        {
            return ProtocolReason.CANCELLED;
        }
        catch (TimeoutException)
        {
            return ProtocolReason.TIMEOUT;
        }
        catch (NetworkException)
        {
            return ProtocolReason.NETWORK_ERROR;
        }
        catch (InvalidOperationException)
        {
            return ProtocolReason.STATE_VIOLATION;
        }
        finally
        {
            transportSession?.ExitNegotiation();
        }
    }

    /// <summary>
    /// Connects the session, then tries resume first and falls back to a full handshake when allowed.
    /// </summary>
    /// <param name="session">The TCP session to connect and resume.</param>
    /// <param name="host">The optional host override.</param>
    /// <param name="port">The optional port override.</param>
    /// <param name="ct">The cancellation token to observe.</param>
    /// <returns><see langword="true"/> when resume succeeded; <see langword="false"/> when a fresh handshake was used.</returns>
    public static async ValueTask<bool> ConnectWithResumeAsync(
        this ITransportSession session,
        string? host = null,
        ushort? port = null,
        CancellationToken ct = default)
        => await session.ConnectWithResumeDetailedAsync(host, port, ct).ConfigureAwait(false) == ProtocolReason.NONE;

    /// <summary>
    /// Connects the session, tries resume when configured and possible, and returns why a full handshake was used.
    /// </summary>
    /// <param name="session">The TCP session to connect and resume.</param>
    /// <param name="host">The optional host override.</param>
    /// <param name="port">The optional port override.</param>
    /// <param name="ct">The cancellation token to observe.</param>
    /// <returns>
    /// <see cref="ProtocolReason.NONE"/> when resume succeeded; otherwise the reason resume was skipped or rejected before fallback handshake completed.
    /// </returns>
    public static async ValueTask<ProtocolReason> ConnectWithResumeDetailedAsync(
        this ITransportSession session,
        string? host = null,
        ushort? port = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await session.ConnectAsync(host, port, ct).ConfigureAwait(false);

        ProtocolReason reason;
        if (!session.Options.ResumeEnabled)
        {
            reason = ProtocolReason.LOCAL_POLICY;
        }
        else if (!HasResumeState(session.State))
        {
            reason = ProtocolReason.SESSION_NOT_FOUND;
        }
        else
        {
            TransportSession? transportSession = session as TransportSession;
            bool suppressDisconnect = session.Options.ResumeFallbackToHandshake && transportSession is not null;
            if (suppressDisconnect)
            {
                transportSession!.EnterSuppressDisconnect();
            }

            try
            {
                reason = await session.ResumeSessionAsync(ct).ConfigureAwait(false);
                if (reason == ProtocolReason.NONE)
                {
                    return ProtocolReason.NONE;
                }

                if (!session.Options.ResumeFallbackToHandshake)
                {
                    throw new NetworkException($"Session resume failed: {reason}");
                }

                if (transportSession is not null)
                {
                    await transportSession.ReconnectForResumeFallbackAsync(host, port, ct).ConfigureAwait(false);
                }
                else
                {
                    if (session.IsConnected)
                    {
                        await session.DisconnectAsync().ConfigureAwait(false);
                    }

                    await session.ConnectAsync(host, port, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (suppressDisconnect)
                {
                    transportSession!.ExitSuppressDisconnect();
                }
            }
        }

        await HandshakeAfterResumeFallbackAsync(session, ct).ConfigureAwait(false);
        return reason;
    }

    // HandshakeAsync sends all handshake frames with explicit encrypt=false and only flips
    // EncryptionEnabled=true once SessionEstablished is verified, so we must NOT mutate
    // the shared session.State.EncryptionEnabled flag here (SEC-06).
    private static ValueTask HandshakeAfterResumeFallbackAsync(ITransportSession session, CancellationToken ct)
        => session.HandshakeAsync(ct);

    /// <summary>
    /// Checks whether the session has enough state to attempt resume.
    /// </summary>
    /// <param name="state">The session state to inspect.</param>
    /// <returns><see langword="true"/> when the session has a token and secret.</returns>
    private static bool HasResumeState(SessionState state) => state.SessionToken != 0 && !state.Secret.IsZero;
}
