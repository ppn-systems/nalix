// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security.Hashing;
using Nalix.Runtime.Handlers;
using Nalix.Runtime.Sessions;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Audit;

/// <summary>
/// Reproduction for session resume audit finding (RT-03).
/// </summary>
[Trait("Category", "Audit")]
public sealed class SessionResumeAuditTests
{
    /// <summary>
    /// RT-03: the SESSION_SIGNAL resume proof is a bearer value (HMAC over token and a 30 s window, sent in
    /// plaintext because the handler is <c>[PacketEncryption(false)]</c>). It is not bound to the resuming
    /// connection, so a request captured on the wire and presented first on a different connection restores the
    /// session's Secret and Level onto the attacker's connection. Expected: the attacker connection stays
    /// unauthenticated.
    /// </summary>
    [Fact]
    public async Task RT03_CapturedResumeProof_CannotRestoreSessionOnAnotherConnection()
    {
        byte[] secretBytes = new byte[32];
        for (int i = 0; i < secretBytes.Length; i++)
        {
            secretBytes[i] = (byte)(i + 1);
        }

        // Legitimate session owner that established a session.
        FakeConnection owner = new();
        ConnectionRuntimeStateAccessor.SetHandshakeEstablished(owner, true);
        owner.Secret = new Bytes32(secretBytes);
        owner.Level = PermissionLevel.ESTABLISHED;

        SessionService sessions = new();
        await sessions.SaveSessionAsync(owner);

        // The proof the legitimate client computes for its resume request (current 30 s window).
        ulong token = owner.ConnectionId;
        long window = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds / 30;
        Span<byte> message = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(message, token);
        BinaryPrimitives.WriteInt64LittleEndian(message[8..], window);
        Span<byte> proof = stackalloc byte[32];
        HmacKeccak256.Compute(secretBytes, message, proof);

        SessionResume captured = new();
        captured.Initialize(SessionResumeStage.REQUEST, token, proof: new Bytes32(proof));

        // Attacker connection presents the captured request first, on its own transport.
        FakeConnection attacker = new();
        var context = PacketContextFactory.Create(captured, attacker, reliable: true);

        FieldInfo field = typeof(SessionHandlers).GetField("s_sessionService", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previous = field.GetValue(null);
        try
        {
            field.SetValue(null, sessions);
            await SessionHandlers.HandleAsync(context);
        }
        finally
        {
            field.SetValue(null, previous);
        }

        attacker.Secret.IsZero.Should().BeTrue(
            "a captured resume proof must not transfer the session secret to another connection");
        attacker.Level.Should().Be(
            PermissionLevel.NONE,
            "a captured resume proof must not elevate another connection to the session level");
    }
}
