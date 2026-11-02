// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Codec.Security.Hashing;

namespace Nalix.Codec.Security;

/// <summary>
/// Provides proof-of-work puzzle solving utilities for Nalix protocol handshakes.
/// </summary>
/// <remarks>
/// This type is intended for client-side use during proof-of-work negotiation.
/// It searches for a counter value that makes the Keccak-256 hash of the puzzle payload
/// satisfy the required number of leading zero bits.
/// </remarks>
public class ProofOfWorkSolver
{
    // Cancellation is observed once per this many attempts, keeping the check off the per-hash path.
    private const long CancellationCheckMask = 0x0FFF;

    /// <summary>
    /// Solves the cryptographic puzzle by finding a solution that satisfies the difficulty condition.
    /// This method blocks until a valid solution is found.
    /// </summary>
    public static long SolveChallenge(ReadOnlySpan<byte> nonce, byte difficulty, long timestampTicks)
        => SolveChallenge(nonce, difficulty, timestampTicks, CancellationToken.None);

    /// <summary>
    /// Solves the cryptographic puzzle, stopping early when <paramref name="ct"/> is cancelled.
    /// This method blocks until a valid solution is found or the token is cancelled.
    /// </summary>
    /// <param name="nonce">The 32-byte challenge nonce.</param>
    /// <param name="difficulty">The number of leading zero bits required in the hash.</param>
    /// <param name="timestampTicks">The challenge timestamp, in ticks.</param>
    /// <param name="ct">A token that bounds the search time.</param>
    /// <returns>The solution that satisfies the difficulty condition.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled before a solution was found.</exception>
    public static long SolveChallenge(ReadOnlySpan<byte> nonce, byte difficulty, long timestampTicks, CancellationToken ct)
    {
        if (difficulty == 0)
        {
            return 0; // Trivial
        }

        Span<byte> puzzle = stackalloc byte[49];
        nonce.CopyTo(puzzle);
        puzzle[32] = difficulty;
        BinaryPrimitives.WriteInt64LittleEndian(puzzle.Slice(33, 8), timestampTicks);

        Span<byte> hash = stackalloc byte[32];
        Span<byte> solutionSpan = puzzle.Slice(41, 8);

        long solution = 0;
        while (true)
        {
            if ((solution & CancellationCheckMask) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            BinaryPrimitives.WriteInt64LittleEndian(solutionSpan, solution);
            Keccak256.HashData(puzzle, hash);

            if (HasLeadingZeroBits(hash, difficulty))
            {
                return solution;
            }

            solution++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasLeadingZeroBits(ReadOnlySpan<byte> hash, byte requiredZeroBits)
    {
        int bytesToCheck = requiredZeroBits / 8;
        int remainingBits = requiredZeroBits % 8;

        for (int i = 0; i < bytesToCheck; i++)
        {
            if (hash[i] != 0)
            {
                return false;
            }
        }

        if (remainingBits > 0)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));
            if ((hash[bytesToCheck] & mask) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
