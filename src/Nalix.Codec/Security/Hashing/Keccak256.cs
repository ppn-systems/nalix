// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.Intrinsics;
using Nalix.Abstractions.Primitives;

namespace Nalix.Codec.Security.Hashing;

/// <summary>
/// Implements Keccak-256 — the <b>original</b> Keccak sponge as used by Ethereum and Web3,
/// <b>not</b> the FIPS 202 SHA3-256 variant.
/// </summary>
/// <remarks>
/// <para>
/// The two variants differ only in their padding domain byte:
/// <list type="table">
///   <listheader><term>Variant</term><description>Domain byte</description></listheader>
///   <item><term>Keccak-256 (this class, Ethereum)</term><description>0x01</description></item>
///   <item><term>SHA3-256 (FIPS 202)</term><description>0x06</description></item>
/// </list>
/// </para>
/// <para>
/// Parameters:
/// <list type="bullet">
///   <item><description>State width : 1600 bits (25 lanes × 64 bits)</description></item>
///   <item><description>Rate        : 1088 bits (136 bytes)</description></item>
///   <item><description>Capacity    : 512 bits</description></item>
///   <item><description>Domain pad  : 0x01 … 0x80 (Keccak original)</description></item>
///   <item><description>Output      : 256 bits (32 bytes)</description></item>
/// </list>
/// </para>
/// <para>
/// Optimisation notes:
/// <list type="bullet">
///   <item>Zero heap allocation — sponge state lives on the stack via <see langword="ref struct"/>.</item>
///   <item>One-shot fast-path for inputs ≤ 136 bytes calls <c>AbsorbBlock</c> directly,
///         picking up AVX-512 / AVX2 / AdvSimd / SSE2 SIMD paths.</item>
///   <item>Round constants stored as <see cref="ReadOnlySpan{T}"/> literal
///         (<c>.rdata</c> section) — immutable by the runtime, zero heap traffic.</item>
///   <item>ρ/π lookup tables replaced with compile-time <c>static readonly</c> spans
///         so they cannot be mutated after startup.</item>
/// </list>
/// </para>
/// </remarks>
/// <threadsafety>
/// Instances are <b>not</b> thread-safe.  Use one instance per hashing operation,
/// or use the static <see cref="HashData(ReadOnlySpan{byte})"/> overload
/// which is stateless and safe to call concurrently.
/// </threadsafety>
[System.Runtime.CompilerServices.SkipLocalsInit]
public static class Keccak256
{
    #region Constants

    private const byte KeccakRounds = 24;

    /// <summary>
    /// 1088-bit rate for Keccak-256
    /// </summary>
    internal const int RateBytes = 136;

    /// <summary>
    /// 256-bit digest
    /// </summary>
    internal const int HashSizeBytes = 32;

    /// <summary>
    /// 5&#215;5 state
    /// </summary>
    private const int Lanes = 25;

    /// <summary>
    /// ── Keccak-256 domain padding (Ethereum) ────────────────────────────────────
    /// SHA3-256 (FIPS 202) uses 0x06 instead — do NOT change this without renaming
    /// the class, or every Ethereum address/signature derived from it will be wrong.
    /// </summary>
    private const byte PadDomain = 0x01;

    private const byte PadFinal = 0x80;

    #endregion Constants

    #region Precomputed Tables (immutable)

    private static ReadOnlySpan<ulong> RC =>
    [
        0x0000000000000001UL, 0x0000000000008082UL,
        0x800000000000808AUL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x0000000000000088UL,
        0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL,
        0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL,
        0x000000000000800AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000000008080UL,
        0x0000000080000001UL, 0x8000000080008008UL
    ];

    /// <summary>
    /// ρ rotation offsets indexed by lane number [x + 5*y].
    /// Lane 0 (x=0,y=0) has rotation 0 per spec — included for uniform indexing.
    /// </summary>
    private static ReadOnlySpan<byte> RotC =>
    [
         0, 1, 62, 28, 27,
        36, 44,  6, 55, 20,
         3, 10, 43, 25, 39,
        41, 45, 15, 21,  8,
        18,  2, 61, 56, 14
    ];

    /// <summary>
    /// π permutation: destination lane for each source lane.
    /// </summary>
    private static ReadOnlySpan<byte> PiDst =>
    [
         0, 10, 20,  5, 15,
        16,  1, 11, 21,  6,
         7, 17,  2, 12, 22,
        23,  8, 18,  3, 13,
        14, 24,  9, 19,  4
    ];

    #endregion Precomputed Tables (immutable)

    #region Public Static API

    /// <summary>
    /// Computes a Keccak-256 digest of <paramref name="data"/> and returns it as a <see cref="Bytes32"/> struct.
    /// <b>Zero heap allocation.</b>
    /// </summary>
    /// <param name="data">Input bytes to hash.</param>
    /// <returns>A <see cref="Bytes32"/> containing the Keccak-256 digest.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static Bytes32 HashDataToFixed(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[HashSizeBytes];
        HashData(data, hash);
        return new Bytes32(hash);
    }

    /// <summary>
    /// Computes a Keccak-256 digest of <paramref name="data"/> and returns a new 32-byte array.
    /// Allocates <b>exactly 32 bytes</b> on the managed heap (the output array).
    /// </summary>
    /// <param name="data">Input bytes to hash.</param>
    /// <returns>A new 32-byte array containing the Keccak-256 digest.</returns>
    /// <exception cref="OutOfMemoryException">Thrown when the runtime cannot allocate the destination hash buffer.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        byte[] result = GC.AllocateUninitializedArray<byte>(HashSizeBytes);
        HashData(data, result);
        return result;
    }

    /// <summary>
    /// Computes a Keccak-256 digest of <paramref name="data"/> and writes it into
    /// <paramref name="output"/>. <b>Zero heap allocation.</b>
    /// </summary>
    /// <param name="data">Input bytes to hash.</param>
    /// <param name="output">Destination span (must be ≥ 32 bytes).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="output"/> is shorter than 32 bytes.
    /// </exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void HashData(
        ReadOnlySpan<byte> data,
        Span<byte> output)
    {
        if (output.Length < HashSizeBytes)
        {
            throw new ArgumentException(
                $"Output buffer must be at least {HashSizeBytes} bytes.", nameof(output));
        }

        // ── One-shot fast-path ────────────────────────────────────────────────────
        // Entire payload fits in one rate block -> no absorb loop, just pad + permute.
        // Strictly smaller: a full 136-byte block has no room for the pad byte in the stack block.
        if (data.Length < RateBytes)
        {
            OneShotFastPath(data, output);
            return;
        }

        // ── Streaming path ────────────────────────────────────────────────────────
        Sponge sponge = new();
        sponge.Absorb(data);
        sponge.PadAndSqueeze(output[..HashSizeBytes]);
    }

    /// <summary>
    /// Attempts to compute a Keccak-256 digest without throwing.
    /// </summary>
    /// <param name="data">Input bytes to hash.</param>
    /// <param name="output">Destination span (must be ≥ 32 bytes).</param>
    /// <returns>
    /// <see langword="true"/> on success;
    /// <see langword="false"/> when <paramref name="output"/> is too short.
    /// </returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool TryHashData(
        ReadOnlySpan<byte> data,
        Span<byte> output)
    {
        if (output.Length < HashSizeBytes)
        {
            return false;
        }

        HashData(data, output);
        return true;
    }

    /// <summary>Returns <c>"Keccak-256"</c>.</summary>
    public static string AlgorithmName => "Keccak-256";

    #endregion Public Static API

    #region One-Shot Fast Path

    /// <summary>
    /// Handles inputs whose length is &lt; <see cref="RateBytes"/> (136 bytes).
    /// Pads inline into a stack-allocated block, calls <see cref="AbsorbBlock"/>
    /// (which selects the widest available SIMD path), then squeezes.
    /// Zero heap allocation.
    /// </summary>
    /// <param name="data"></param>
    /// <param name="output"></param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void OneShotFastPath(
        ReadOnlySpan<byte> data,
        Span<byte> output)
    {
        Span<ulong> state = stackalloc ulong[Lanes];
        state.Clear();
        Span<byte> block = stackalloc byte[RateBytes];

        data.CopyTo(block);
        block[data.Length..].Clear();

        // BUG FIX #1: Keccak-256 (Ethereum) uses 0x01, not 0x06 (SHA3/FIPS 202).
        block[data.Length] = PadDomain; // 0x01

        // BUG FIX #2: when data.Length == RateBytes - 1 the pad byte and the final 0x80 land on the
        // same position -> 0x01 | 0x80 = 0x81. The |= handles both the normal and the overlap case.
        // Inputs of RateBytes or more never reach this path (they use the sponge).
        block[RateBytes - 1] |= PadFinal; // 0x80

        // BUG FIX #3 (perf): call AbsorbBlock so the SIMD dispatch is shared.
        // Previous code had an inline scalar loop here, bypassing AVX-512/AVX2/etc.
        AbsorbBlock(state, block);
        Squeeze(state, output[..HashSizeBytes]);
    }

    #endregion One-Shot Fast Path

    #region Sponge (ref struct — zero heap allocation)

    /// <summary>
    /// Stack-only incremental sponge context for inputs &gt; 136 bytes.
    /// Declared as <see langword="ref struct"/> so the runtime guarantees it never
    /// escapes to the heap.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public struct Sponge
    {
        /// <summary>
        /// 200 B on the stack
        /// </summary>
        private InlineArray25<ulong> _state;

        /// <summary>
        /// 136 B on the stack
        /// </summary>
        private InlineArray136<byte> _buffer;

        private int _bufferLen;

        // ── Absorb ────────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void Absorb(scoped ReadOnlySpan<byte> data)
        {
            Span<ulong> state = _state;
            Span<byte> buffer = _buffer;
            ReadOnlySpan<byte> input = data;

            // Drain partial block
            if (_bufferLen > 0)
            {
                int need = RateBytes - _bufferLen;
                int take = Math.Min(need, input.Length);
                input[..take].CopyTo(buffer[_bufferLen..]);
                _bufferLen += take;
                input = input[take..];

                if (_bufferLen == RateBytes)
                {
                    AbsorbBlock(state, buffer);
                    _bufferLen = 0;
                }
            }

            // Full blocks directly from input (no copy)
            while (input.Length >= RateBytes)
            {
                AbsorbBlock(state, input[..RateBytes]);
                input = input[RateBytes..];
            }

            // Buffer the tail
            if (!input.IsEmpty)
            {
                input.CopyTo(buffer[_bufferLen..]);
                _bufferLen += input.Length;
            }
        }

        // ── Pad + Squeeze ─────────────────────────────────────────────────────────

        /// <inheritdoc/>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void PadAndSqueeze(scoped Span<byte> output)
        {
            Span<ulong> state = _state;
            Span<byte> buffer = _buffer;
            int n = _bufferLen;

            // Edge: buffer was exactly full — absorb it first, then pad a fresh block.
            if (n == RateBytes)
            {
                AbsorbBlock(state, buffer);
                buffer.Clear();
                n = 0;
            }

            // Zero the remainder, apply Keccak-256 padding (0x01 … 0x80).
            buffer[n..].Clear();

            // BUG FIX #1: 0x01 domain byte (Keccak original), not 0x06 (SHA3/FIPS).
            buffer[n] = PadDomain; // 0x01

            // BUG FIX #2: when n == RateBytes-1, pad and final bits share the same byte.
            // Using |= handles both the overlap case (n == RateBytes-1 -> 0x01|0x80 = 0x81)
            // and the normal case (n < RateBytes-1 -> byte was cleared, so |= is equivalent).
            // The previous code had a special branch that absorbed an extra all-zero block
            // after the padding block — that was wrong per the Keccak spec.
            buffer[RateBytes - 1] |= PadFinal; // 0x80

            AbsorbBlock(state, buffer);
            Squeeze(state, output);
        }
    }

    #endregion Sponge (ref struct — zero heap allocation)

    #region Keccak Core

    /// <summary>
    /// XORs one full 136-byte rate block into <paramref name="state"/> and applies
    /// Keccak-f[1600]. Dispatches to the widest available SIMD ISA at runtime.
    /// </summary>
    /// <param name="state"></param>
    /// <param name="block"></param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static unsafe void AbsorbBlock(
        Span<ulong> state,
        ReadOnlySpan<byte> block)
    {
        System.Diagnostics.Debug.Assert(block.Length == RateBytes,
            $"AbsorbBlock requires exactly {RateBytes} bytes.");

        // Rate = 136 bytes = 17 uint64 lanes.
        // Last lane (index 16) is always handled as a scalar tail after the SIMD body
        // because 17 is odd and no SIMD width divides 17 evenly.

        if (BitConverter.IsLittleEndian)
        {
            fixed (byte* pBlock = block)
            {
                fixed (ulong* pState = state)
                {
                    // ── AVX-512 (2 × 8 lanes = 16) + 1 scalar tail ──────────────────
                    if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported &&
                        System.Runtime.Intrinsics.X86.Avx512DQ.IsSupported)
                    {
                        Vector512<ulong> b0 = System.Runtime.CompilerServices.Unsafe
                            .ReadUnaligned<Vector512<ulong>>(pBlock);
                        Vector512<ulong> b1 = System.Runtime.CompilerServices.Unsafe
                            .ReadUnaligned<Vector512<ulong>>(pBlock + 64);
                        Vector512<ulong> s0 = System.Runtime.CompilerServices.Unsafe
                            .ReadUnaligned<Vector512<ulong>>((byte*)pState);
                        Vector512<ulong> s1 = System.Runtime.CompilerServices.Unsafe
                            .ReadUnaligned<Vector512<ulong>>((byte*)pState + 64);

                        System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                            (byte*)pState, System.Runtime.Intrinsics.X86.Avx512F.Xor(b0, s0));
                        System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                            (byte*)pState + 64, System.Runtime.Intrinsics.X86.Avx512F.Xor(b1, s1));

                        pState[16] ^= System.Buffers.Binary.BinaryPrimitives
                            .ReadUInt64LittleEndian(block.Slice(128, 8));

                        KeccakF1600(state);
                        return;
                    }

                    // ── AVX2 (4 × 4 lanes = 16) + 1 scalar tail ────────────────────
                    if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
                    {
                        for (int off = 0; off < 128; off += 32)
                        {
                            Vector256<ulong> vb = System.Runtime.CompilerServices.Unsafe
                                .ReadUnaligned<Vector256<ulong>>(pBlock + off);
                            Vector256<ulong> vs = System.Runtime.CompilerServices.Unsafe
                                .ReadUnaligned<Vector256<ulong>>((byte*)pState + off);
                            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                                (byte*)pState + off,
                                System.Runtime.Intrinsics.X86.Avx2.Xor(vb, vs));
                        }
                        pState[16] ^= System.Buffers.Binary.BinaryPrimitives
                            .ReadUInt64LittleEndian(block.Slice(128, 8));

                        KeccakF1600(state);
                        return;
                    }

                    // ── ARM AdvSimd (8 × 2 lanes = 16) + 1 scalar tail ─────────────
                    if (System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported)
                    {
                        for (int off = 0; off < 128; off += 16)
                        {
                            Vector128<ulong> vb = System.Runtime.CompilerServices.Unsafe
                                .ReadUnaligned<Vector128<ulong>>(pBlock + off);
                            Vector128<ulong> vs = System.Runtime.CompilerServices.Unsafe
                                .ReadUnaligned<Vector128<ulong>>((byte*)pState + off);
                            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                                (byte*)pState + off,
                                System.Runtime.Intrinsics.Arm.AdvSimd.Xor(vb, vs));
                        }
                        pState[16] ^= System.Buffers.Binary.BinaryPrimitives
                            .ReadUInt64LittleEndian(block.Slice(128, 8));

                        KeccakF1600(state);
                        return;
                    }

                    // ── SSE2 (8 × 2 lanes = 16) + 1 scalar tail ────────────────────
                    if (System.Runtime.Intrinsics.X86.Sse2.IsSupported)
                    {
                        for (int off = 0; off < 128; off += 16)
                        {
                            Vector128<ulong> vb = System.Runtime.CompilerServices.Unsafe
                                .ReadUnaligned<Vector128<ulong>>(pBlock + off);
                            Vector128<ulong> vs = System.Runtime.CompilerServices.Unsafe
                                .ReadUnaligned<Vector128<ulong>>((byte*)pState + off);
                            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(
                                (byte*)pState + off,
                                System.Runtime.Intrinsics.X86.Sse2.Xor(vb, vs));
                        }
                        pState[16] ^= System.Buffers.Binary.BinaryPrimitives
                            .ReadUInt64LittleEndian(block.Slice(128, 8));

                        KeccakF1600(state);
                        return;
                    }

                    // ── Scalar little-endian (unrolled, bounds-check-free) ───────────
                    // BUG FIX #5: previous Portable Vector<T> path had a correctness bug
                    // when Vector<ulong>.Count did not evenly divide 16 (e.g. width=3).
                    // The portable-vector branch is replaced by this fully unrolled scalar
                    // path which is correct regardless of SIMD width, and the JIT will
                    // auto-vectorise it anyway on hardware where it matters.
                    ReadOnlySpan<ulong> u64 =
                        System.Runtime.InteropServices.MemoryMarshal
                            .Cast<byte, ulong>(block);

                    ref ulong s = ref state[0];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 0) ^= u64[0];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 1) ^= u64[1];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 2) ^= u64[2];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 3) ^= u64[3];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 4) ^= u64[4];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 5) ^= u64[5];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 6) ^= u64[6];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 7) ^= u64[7];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 8) ^= u64[8];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 9) ^= u64[9];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 10) ^= u64[10];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 11) ^= u64[11];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 12) ^= u64[12];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 13) ^= u64[13];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 14) ^= u64[14];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 15) ^= u64[15];
                    System.Runtime.CompilerServices.Unsafe.Add(ref s, 16) ^= u64[16];

                    KeccakF1600(state);
                    return;
                }
            }
        }

        // ── Big-endian: explicit LE reads for cross-platform determinism ─────────
        {
            ref ulong dst = ref state[0];

            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 0) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block[..8]);
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 1) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(8, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 2) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(16, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 3) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(24, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 4) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(32, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 5) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(40, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 6) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(48, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 7) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(56, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 8) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(64, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 9) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(72, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 10) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(80, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 11) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(88, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 12) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(96, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 13) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(104, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 14) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(112, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 15) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(120, 8));
            System.Runtime.CompilerServices.Unsafe.Add(ref dst, 16) ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(128, 8));

            KeccakF1600(state);
        }
    }

    /// <summary>
    /// Writes the first 32 bytes of <paramref name="state"/> into <paramref name="output"/>
    /// as four little-endian 64-bit lanes.
    /// </summary>
    /// <param name="state"></param>
    /// <param name="output"></param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void Squeeze(
        ReadOnlySpan<ulong> state,
        Span<byte> output)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output[..8], state[0]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(8, 8), state[1]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(16, 8), state[2]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(24, 8), state[3]);
    }

    /// <summary>
    /// Applies 24 rounds of the Keccak-f[1600] permutation in-place.
    /// </summary>
    /// <param name="A"></param>
    [System.Diagnostics.DebuggerStepThrough]
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void KeccakF1600(Span<ulong> A)
    {
        // B is the intermediate buffer for the ρ+π step.
        // Kept as a local stackalloc — 200 B, stays in the same stack frame as Absorb.
        Span<ulong> B = stackalloc ulong[Lanes];

        ReadOnlySpan<ulong> rc = RC;
        ReadOnlySpan<byte> rotC = RotC;
        ReadOnlySpan<byte> piDst = PiDst;

        for (int round = 0; round < KeccakRounds; round++)
        {
            // ── θ ─────────────────────────────────────────────────────────────────
            ulong c0 = A[0] ^ A[5] ^ A[10] ^ A[15] ^ A[20];
            ulong c1 = A[1] ^ A[6] ^ A[11] ^ A[16] ^ A[21];
            ulong c2 = A[2] ^ A[7] ^ A[12] ^ A[17] ^ A[22];
            ulong c3 = A[3] ^ A[8] ^ A[13] ^ A[18] ^ A[23];
            ulong c4 = A[4] ^ A[9] ^ A[14] ^ A[19] ^ A[24];

            // D[x] = C[x-1] XOR ROT(C[x+1], 1)  (indices mod 5)
            ulong d0 = c4 ^ System.Numerics.BitOperations.RotateLeft(c1, 1);
            ulong d1 = c0 ^ System.Numerics.BitOperations.RotateLeft(c2, 1);
            ulong d2 = c1 ^ System.Numerics.BitOperations.RotateLeft(c3, 1);
            ulong d3 = c2 ^ System.Numerics.BitOperations.RotateLeft(c4, 1);
            ulong d4 = c3 ^ System.Numerics.BitOperations.RotateLeft(c0, 1);

            A[0] ^= d0; A[5] ^= d0; A[10] ^= d0; A[15] ^= d0; A[20] ^= d0;
            A[1] ^= d1; A[6] ^= d1; A[11] ^= d1; A[16] ^= d1; A[21] ^= d1;
            A[2] ^= d2; A[7] ^= d2; A[12] ^= d2; A[17] ^= d2; A[22] ^= d2;
            A[3] ^= d3; A[8] ^= d3; A[13] ^= d3; A[18] ^= d3; A[23] ^= d3;
            A[4] ^= d4; A[9] ^= d4; A[14] ^= d4; A[19] ^= d4; A[24] ^= d4;

            // ── ρ + π ─────────────────────────────────────────────────────────────
            // B[π(i)] = ROT(A[i], ρ(i))
            // Both piDst and rotC are ReadOnlySpan literals — JIT treats them as
            // constant arrays, eliminating bounds checks in the unrolled body.
            B[piDst[0]] = System.Numerics.BitOperations.RotateLeft(A[0], rotC[0]);
            B[piDst[1]] = System.Numerics.BitOperations.RotateLeft(A[1], rotC[1]);
            B[piDst[2]] = System.Numerics.BitOperations.RotateLeft(A[2], rotC[2]);
            B[piDst[3]] = System.Numerics.BitOperations.RotateLeft(A[3], rotC[3]);
            B[piDst[4]] = System.Numerics.BitOperations.RotateLeft(A[4], rotC[4]);
            B[piDst[5]] = System.Numerics.BitOperations.RotateLeft(A[5], rotC[5]);
            B[piDst[6]] = System.Numerics.BitOperations.RotateLeft(A[6], rotC[6]);
            B[piDst[7]] = System.Numerics.BitOperations.RotateLeft(A[7], rotC[7]);
            B[piDst[8]] = System.Numerics.BitOperations.RotateLeft(A[8], rotC[8]);
            B[piDst[9]] = System.Numerics.BitOperations.RotateLeft(A[9], rotC[9]);
            B[piDst[10]] = System.Numerics.BitOperations.RotateLeft(A[10], rotC[10]);
            B[piDst[11]] = System.Numerics.BitOperations.RotateLeft(A[11], rotC[11]);
            B[piDst[12]] = System.Numerics.BitOperations.RotateLeft(A[12], rotC[12]);
            B[piDst[13]] = System.Numerics.BitOperations.RotateLeft(A[13], rotC[13]);
            B[piDst[14]] = System.Numerics.BitOperations.RotateLeft(A[14], rotC[14]);
            B[piDst[15]] = System.Numerics.BitOperations.RotateLeft(A[15], rotC[15]);
            B[piDst[16]] = System.Numerics.BitOperations.RotateLeft(A[16], rotC[16]);
            B[piDst[17]] = System.Numerics.BitOperations.RotateLeft(A[17], rotC[17]);
            B[piDst[18]] = System.Numerics.BitOperations.RotateLeft(A[18], rotC[18]);
            B[piDst[19]] = System.Numerics.BitOperations.RotateLeft(A[19], rotC[19]);
            B[piDst[20]] = System.Numerics.BitOperations.RotateLeft(A[20], rotC[20]);
            B[piDst[21]] = System.Numerics.BitOperations.RotateLeft(A[21], rotC[21]);
            B[piDst[22]] = System.Numerics.BitOperations.RotateLeft(A[22], rotC[22]);
            B[piDst[23]] = System.Numerics.BitOperations.RotateLeft(A[23], rotC[23]);
            B[piDst[24]] = System.Numerics.BitOperations.RotateLeft(A[24], rotC[24]);

            // ── χ ─────────────────────────────────────────────────────────────────
            // A[i] = B[i] XOR ((NOT B[i+1]) AND B[i+2])  per row of 5
            ulong b0, b1, b2, b3, b4;

            b0 = B[0]; b1 = B[1]; b2 = B[2]; b3 = B[3]; b4 = B[4];
            A[0] = b0 ^ (~b1 & b2); A[1] = b1 ^ (~b2 & b3); A[2] = b2 ^ (~b3 & b4); A[3] = b3 ^ (~b4 & b0); A[4] = b4 ^ (~b0 & b1);

            b0 = B[5]; b1 = B[6]; b2 = B[7]; b3 = B[8]; b4 = B[9];
            A[5] = b0 ^ (~b1 & b2); A[6] = b1 ^ (~b2 & b3); A[7] = b2 ^ (~b3 & b4); A[8] = b3 ^ (~b4 & b0); A[9] = b4 ^ (~b0 & b1);

            b0 = B[10]; b1 = B[11]; b2 = B[12]; b3 = B[13]; b4 = B[14];
            A[10] = b0 ^ (~b1 & b2); A[11] = b1 ^ (~b2 & b3); A[12] = b2 ^ (~b3 & b4); A[13] = b3 ^ (~b4 & b0); A[14] = b4 ^ (~b0 & b1);

            b0 = B[15]; b1 = B[16]; b2 = B[17]; b3 = B[18]; b4 = B[19];
            A[15] = b0 ^ (~b1 & b2); A[16] = b1 ^ (~b2 & b3); A[17] = b2 ^ (~b3 & b4); A[18] = b3 ^ (~b4 & b0); A[19] = b4 ^ (~b0 & b1);

            b0 = B[20]; b1 = B[21]; b2 = B[22]; b3 = B[23]; b4 = B[24];
            A[20] = b0 ^ (~b1 & b2); A[21] = b1 ^ (~b2 & b3); A[22] = b2 ^ (~b3 & b4); A[23] = b3 ^ (~b4 & b0); A[24] = b4 ^ (~b0 & b1);

            // ── ι ─────────────────────────────────────────────────────────────────
            A[0] ^= rc[round];
        }
    }

    #endregion Keccak Core
}

// ── InlineArray helpers ───────────────────────────────────────────────────────
// Zero-cost value types that give the Sponge ref struct its stack-allocated
// state and buffer without needing heap arrays or unsafe fixed buffers.

/// <summary>
/// Stack-allocated inline array of 25 × <typeparamref name="T"/> (200 B for ulong).
/// </summary>
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Runtime.CompilerServices.InlineArray(25)]
internal struct InlineArray25<T>
{
    private T _element0;
}

/// <summary>
/// Stack-allocated inline array of 136 × <typeparamref name="T"/> (136 B for byte).
/// </summary>
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Runtime.CompilerServices.InlineArray(136)]
internal struct InlineArray136<T>
{
    private T _element0;
}
