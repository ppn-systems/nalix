// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Hashing;

namespace Nalix.Codec.Tests.Security.Hashing;

/// <summary>
/// Exercises the session-resume proof-of-possession mechanism used by
/// <c>Nalix.SDK.Transport.Extensions.ResumeExtensions</c> (HMAC-Keccak256 over
/// <c>SessionToken(8B LE) || UnixSecondsNow()/30(8B LE)</c>, i.e. a 30-second replay/expiry time
/// bucket) directly against <see cref="HmacKeccak256"/>, since the full session/transport
/// plumbing (<c>TransportSession</c>, live socket, packet awaiter) is not practical to
/// instantiate from a unit test — this isolates and verifies the cryptographic mechanism itself:
/// tamper/wrong-token/wrong-time-bucket rejection and same-bucket replay acceptance.
/// </summary>
public sealed class HmacKeccak256SessionResumeProofTests
{
    private static Bytes32 ComputeProof(Bytes32 secret, ulong sessionToken, long timeBucket)
    {
        Span<byte> message = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(message, sessionToken);
        BinaryPrimitives.WriteInt64LittleEndian(message[8..], timeBucket);

        Span<byte> proof = stackalloc byte[32];
        HmacKeccak256.Compute(secret.AsSpan(), message, proof);
        return new Bytes32(proof);
    }

    private static Bytes32 Secret(byte seed)
    {
        byte[] raw = new byte[32];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(seed + i);
        }

        return new Bytes32(raw);
    }

    [Fact]
    public void Proof_WithSameInputs_IsReproducible()
    {
        Bytes32 secret = Secret(1);
        Bytes32 proof = ComputeProof(secret, sessionToken: 12345, timeBucket: 1000);
        Bytes32 recomputed = ComputeProof(secret, sessionToken: 12345, timeBucket: 1000);

        Assert.Equal(proof.AsSpan().ToArray(), recomputed.AsSpan().ToArray());
    }

    [Fact]
    public void Proof_WithDifferentSessionToken_Differs()
    {
        Bytes32 secret = Secret(2);
        Bytes32 proof = ComputeProof(secret, sessionToken: 1, timeBucket: 1000);
        Bytes32 wrongToken = ComputeProof(secret, sessionToken: 2, timeBucket: 1000);

        Assert.NotEqual(proof.AsSpan().ToArray(), wrongToken.AsSpan().ToArray());
    }

    [Fact]
    public void Proof_WithDifferentTimeBucket_Differs()
    {
        Bytes32 secret = Secret(3);
        Bytes32 proof = ComputeProof(secret, sessionToken: 1, timeBucket: 1000);
        Bytes32 nextBucket = ComputeProof(secret, sessionToken: 1, timeBucket: 1001);

        Assert.NotEqual(proof.AsSpan().ToArray(), nextBucket.AsSpan().ToArray());
    }

    [Fact]
    public void Proof_WithDifferentSecret_Differs()
    {
        Bytes32 proofA = ComputeProof(Secret(4), sessionToken: 1, timeBucket: 1000);
        Bytes32 proofB = ComputeProof(Secret(5), sessionToken: 1, timeBucket: 1000);

        Assert.NotEqual(proofA.AsSpan().ToArray(), proofB.AsSpan().ToArray());
    }

    /// <summary>
    /// Behavioral finding, not a crash bug: the 30-second time bucket is the ONLY anti-replay
    /// mechanism for resume proofs — there is no nonce/one-time-use tracking. A proof computed
    /// for a given (secret, token, bucket) is deterministic and valid for the ENTIRE 30-second
    /// bucket, and is trivially reproducible again in that same window. This documents that a
    /// captured resume request can be replayed by an attacker within the same time bucket.
    /// </summary>
    [Fact]
    public void Proof_WithinSameTimeBucket_CanBeReplayedIndefinitely()
    {
        Bytes32 secret = Secret(6);
        Bytes32 first = ComputeProof(secret, sessionToken: 99, timeBucket: 5000);
        Bytes32 second = ComputeProof(secret, sessionToken: 99, timeBucket: 5000);
        Bytes32 third = ComputeProof(secret, sessionToken: 99, timeBucket: 5000);

        Assert.Equal(first.AsSpan().ToArray(), second.AsSpan().ToArray());
        Assert.Equal(first.AsSpan().ToArray(), third.AsSpan().ToArray());
    }
}
