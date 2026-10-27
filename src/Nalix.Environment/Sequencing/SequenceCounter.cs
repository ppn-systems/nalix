// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;

namespace Nalix.Environment.Sequencing;

/// <summary>
/// Provides a thread-safe, monotonically increasing sequence number generator
/// for use with packet encryption per session or connection.
/// </summary>
/// <remarks>
/// <para>
/// This class is designed to be used separately for each communication direction
/// (send and receive) to prevent nonce/counter reuse attacks.
/// </para>
/// <list type="bullet">
/// <item>Starts at 1 by default (after first <see cref="Next"/> call)</item>
/// <item>Never repeats values for the lifetime of the instance</item>
/// <item>Fully thread-safe and lock-free using <see cref="Interlocked"/> CAS on a packed 64-bit state.</item>
/// <item>Critical for security when using stream ciphers (ChaCha20, Salsa20, etc.)</item>
/// </list>
/// </remarks>
public sealed class SequenceCounter : ISequenceCounter
{
    // High 32-bits: Sequence Number
    // Low 32-bits: Sliding Window Bitmap (for replay protection)
    private ulong _packedState;

    /// <summary>
    /// Initializes a new instance of the <see cref="SequenceCounter"/> class.
    /// </summary>
    /// <param name="initialValue">
    /// The initial value of the counter. 
    /// The first call to <see cref="Next"/> will return <paramref name="initialValue"/> + 1.
    /// Default is 0.
    /// </param>
    public SequenceCounter(uint initialValue = 0) => _packedState = (ulong)initialValue << 32;

    /// <summary>
    /// Returns the next sequence number and increments the counter atomically.
    /// </summary>
    /// <returns>The next monotonic sequence number.</returns>
    /// <exception cref="CipherException">Thrown if the counter overflows.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Next()
    {
        // CAS loop that refuses to advance past uint.MaxValue. A plain Interlocked.Add would carry out of
        // the high word, wrapping the stored sequence to 0 so later calls would reissue 1, 2, ... (nonce reuse).
        ulong state;
        do
        {
            state = Volatile.Read(ref _packedState);
            if ((uint)(state >> 32) == uint.MaxValue)
            {
                throw new CipherException("Sequence counter overflow. Key rotation is required to prevent nonce reuse.");
            }
        }
        while (Interlocked.CompareExchange(ref _packedState, state + (1UL << 32), state) != state);

        return (uint)(state >> 32) + 1;
    }

    /// <summary>
    /// Returns the current sequence number without incrementing it.
    /// </summary>
    /// <returns>The current value of the counter.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Current() => (uint)(Volatile.Read(ref _packedState) >> 32);

    /// <summary>
    /// Resets the counter to a new value.
    /// </summary>
    /// <param name="newValue">The new value to set.</param>
    /// <remarks>
    /// <b>Warning:</b> Should only be used when performing a full key rotation.
    /// Resetting without changing the key may open replay attack vectors.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(uint newValue = 0) => Volatile.Write(ref _packedState, (ulong)newValue << 32);

    /// <summary>
    /// Validates whether a received sequence number is valid (helps prevent replay attacks).
    /// </summary>
    /// <param name="receivedSeq">The sequence number received from the remote party.</param>
    /// <param name="window">
    /// Allowed reordering window. 
    /// Use a small value (e.g. 32) if your protocol allows out-of-order packets.
    /// Max effective window is 32. Default is 0 (strictly monotonic).
    /// </param>
    /// <returns><c>true</c> if the sequence number is valid; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsValid(uint? receivedSeq, uint window = 0)
    {
        if (receivedSeq == null)
        {
            return true;
        }

        uint seq = receivedSeq.Value;
        ulong state = Volatile.Read(ref _packedState);
        uint current = (uint)(state >> 32);
        uint bitmap = (uint)state;

        if (current == 0)
        {
            return true;
        }

        if (seq == 0)
        {
            return false;
        }

        if (seq > current)
        {
            return true;
        }

        if (window > 0 && current - seq < window)
        {
            int shift = (int)(current - seq);
            if (shift >= 32)
            {
                return false;
            }

            uint mask = 1U << shift;
            return (bitmap & mask) == 0;
        }

        return false;
    }

    /// <summary>
    /// Updates the internal counter and window bitmap lock-free.
    /// Should be called after successfully decrypting and validating a packet.
    /// </summary>
    /// <param name="receivedSeq">The sequence number from a successfully decrypted packet.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateTo(uint receivedSeq)
    {
        ulong currentState;
        ulong newState;
        do
        {
            currentState = Volatile.Read(ref _packedState);
            uint currentSeq = (uint)(currentState >> 32);
            uint currentBitmap = (uint)currentState;

            if (receivedSeq > currentSeq)
            {
                int shift = (int)(receivedSeq - currentSeq);
                uint newBitmap = shift >= 32 ? 1U : (currentBitmap << shift) | 1U;
                newState = ((ulong)receivedSeq << 32) | newBitmap;
            }
            else
            {
                int shift = (int)(currentSeq - receivedSeq);
                if (shift >= 32)
                {
                    return; // Outside window, cannot track
                }

                uint mask = 1U << shift;
                if ((currentBitmap & mask) != 0)
                {
                    return; // Already processed
                }

                uint newBitmap = currentBitmap | mask;
                newState = ((ulong)currentSeq << 32) | newBitmap;
            }

        } while (Interlocked.CompareExchange(ref _packedState, newState, currentState) != currentState);
    }

    /// <summary>
    /// Resumes the counter from a previously saved value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResumeFrom(uint lastKnownSeq, uint safetyGap = 1000)
    {
        if (lastKnownSeq == 0)
        {
            return;
        }

        ulong currentState;
        ulong newState;
        do
        {
            currentState = Volatile.Read(ref _packedState);
            uint currentSeq = (uint)(currentState >> 32);

            // Saturate instead of wrapping: an unchecked wrap would resume below
            // lastKnownSeq and reissue already-used sequence numbers (nonce reuse).
            // At uint.MaxValue the next call to Next() throws CipherException,
            // forcing key rotation via the existing overflow policy.
            ulong candidate = (ulong)lastKnownSeq + safetyGap;
            uint newValue = candidate >= uint.MaxValue ? uint.MaxValue : (uint)candidate;

            if (newValue <= currentSeq)
            {
                return;
            }

            newState = ((ulong)newValue << 32) | 1U;
        } while (Interlocked.CompareExchange(ref _packedState, newState, currentState) != currentState);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsApproachingOverflow(uint margin = 1_000_000)
        => this.Current() >= uint.MaxValue - margin;
}
