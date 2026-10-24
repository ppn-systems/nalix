// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security;

namespace Nalix.Codec.Tests.Security;

/// <summary>
/// Edge-case, misuse, and replay-policy tests for <see cref="ProofOfWork"/>.
/// There was no prior test coverage for this type.
/// </summary>
public sealed class ProofOfWorkTests
{
    private static long SolveOrGiveUp(ReadOnlySpan<byte> nonce, byte difficulty, long timestampTicks)
    {
        try
        {
            return ProofOfWorkSolver.SolveChallenge(nonce, difficulty, timestampTicks);
        }
        catch
        {
            return 0;
        }
    }

    [Fact]
    public void VerifySolution_WithDifficultyZero_AcceptsWithoutSolving()
    {
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(difficulty: 0, connectionId: 1, timestampTicks: 1000);

        bool ok = ProofOfWork.VerifySolution(nonce.AsSpan(), difficulty: 0, timestampTicks: 1000, connectionId: 1, mac.AsSpan(), solution: 0);

        Assert.True(ok);
    }

    [Fact]
    public void VerifySolution_WithMaxDifficultyAndNonSolvingValue_ReturnsFalse()
    {
        const byte difficulty = 255;
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(difficulty, connectionId: 2, timestampTicks: 2000);

        // A 255-leading-zero-bit hash (out of a 256-bit Keccak digest) is computationally
        // infeasible to find; instead verify the MAC/replay layer alone using an obviously
        // non-solving value — solution acceptance must fail at the puzzle-check stage, not throw.
        bool ok = ProofOfWork.VerifySolution(nonce.AsSpan(), difficulty, timestampTicks: 2000, connectionId: 2, mac.AsSpan(), solution: 0);

        Assert.False(ok);
    }

    [Fact]
    public void VerifySolution_WhenNonceIsTampered_ReturnsFalse()
    {
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(difficulty: 8, connectionId: 3, timestampTicks: 3000);
        byte[] tamperedNonce = nonce.AsSpan().ToArray();
        tamperedNonce[0] ^= 0xFF;

        long solution = SolveOrGiveUp(tamperedNonce, 8, 3000);
        bool ok = ProofOfWork.VerifySolution(tamperedNonce, difficulty: 8, timestampTicks: 3000, connectionId: 3, mac.AsSpan(), solution);

        Assert.False(ok);
    }

    [Fact]
    public void VerifySolution_WhenMacIsTampered_ReturnsFalse()
    {
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(difficulty: 0, connectionId: 4, timestampTicks: 4000);
        byte[] tamperedMac = mac.AsSpan().ToArray();
        tamperedMac[0] ^= 0xFF;

        bool ok = ProofOfWork.VerifySolution(nonce.AsSpan(), difficulty: 0, timestampTicks: 4000, connectionId: 4, tamperedMac, solution: 0);

        Assert.False(ok);
    }

    [Fact]
    public void VerifySolution_WhenTimestampIsTampered_ReturnsFalse()
    {
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(difficulty: 0, connectionId: 5, timestampTicks: 5000);

        bool ok = ProofOfWork.VerifySolution(nonce.AsSpan(), difficulty: 0, timestampTicks: 5001, connectionId: 5, mac.AsSpan(), solution: 0);

        Assert.False(ok);
    }

    [Fact]
    public void VerifySolution_WhenConnectionIdIsTampered_ReturnsFalse()
    {
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(difficulty: 0, connectionId: 6, timestampTicks: 6000);

        bool ok = ProofOfWork.VerifySolution(nonce.AsSpan(), difficulty: 0, timestampTicks: 6000, connectionId: 999, mac.AsSpan(), solution: 0);

        Assert.False(ok);
    }

    [Fact]
    public void VerifySolution_WithProofForDifferentChallenge_ReturnsFalse()
    {
        (Bytes32 nonceA, Bytes32 macA) = ProofOfWork.CreateChallenge(difficulty: 4, connectionId: 7, timestampTicks: 7000);

        for (int i = 0; i < 16; i++)
        {
            (Bytes32 nonceB, _) = ProofOfWork.CreateChallenge(difficulty: 4, connectionId: 7, timestampTicks: 7000 + i);
            long solutionForB = SolveOrGiveUp(nonceB.AsSpan().ToArray(), 4, 7000 + i);

            // solution solved for nonceB's puzzle, submitted against challenge A's nonce+mac.
            bool ok = ProofOfWork.VerifySolution(nonceA.AsSpan(), difficulty: 4, timestampTicks: 7000, connectionId: 7, macA.AsSpan(), solutionForB);
            if (!ok)
            {
                return;
            }
        }

        throw new InvalidOperationException("Unable to generate a distinct low-difficulty proof sample.");
    }

    /// <summary>
    /// Behavioral finding, not a crash bug: <see cref="ProofOfWork.VerifySolution"/> is a pure,
    /// stateless function with no nonce-tracking store. A previously-accepted (nonce, mac,
    /// solution) tuple verifies successfully every time it is replayed, because nothing in this
    /// type enforces single-use or a timestamp freshness window — the caller is responsible for
    /// tracking used nonces / enforcing a timestamp window externally. This test documents that
    /// unlimited replay is currently accepted at this layer.
    /// </summary>
    [Fact]
    public void VerifySolution_WhenProofIsReplayed_AcceptsEveryTime()
    {
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(difficulty: 0, connectionId: 8, timestampTicks: 8000);

        bool first = ProofOfWork.VerifySolution(nonce.AsSpan(), 0, 8000, 8, mac.AsSpan(), 0);
        bool second = ProofOfWork.VerifySolution(nonce.AsSpan(), 0, 8000, 8, mac.AsSpan(), 0);
        bool third = ProofOfWork.VerifySolution(nonce.AsSpan(), 0, 8000, 8, mac.AsSpan(), 0);

        Assert.True(first);
        Assert.True(second);
        Assert.True(third);
    }

    [Fact]
    public void SolveChallenge_WithLowDifficulty_ProducesVerifiableSolution()
    {
        const byte difficulty = 8; // small enough to solve quickly in a unit test
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(difficulty, connectionId: 9, timestampTicks: 9000);

        long solution = ProofOfWorkSolver.SolveChallenge(nonce.AsSpan(), difficulty, timestampTicks: 9000);
        bool ok = ProofOfWork.VerifySolution(nonce.AsSpan(), difficulty, timestampTicks: 9000, connectionId: 9, mac.AsSpan(), solution);

        Assert.True(ok);
    }

    [Fact]
    public void VerifySolution_WithWrongLengthNonce_ReturnsFalse()
    {
        byte[] shortNonce = new byte[16];
        byte[] mac = new byte[32];

        bool ok = ProofOfWork.VerifySolution(shortNonce, 0, 0, 0, mac, 0);

        Assert.False(ok);
    }

    [Fact]
    public void VerifySolution_WithWrongLengthMac_ReturnsFalse()
    {
        byte[] nonce = new byte[32];
        byte[] shortMac = new byte[16];

        bool ok = ProofOfWork.VerifySolution(nonce, 0, 0, 0, shortMac, 0);

        Assert.False(ok);
    }
}
