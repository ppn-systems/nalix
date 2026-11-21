// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nalix.Codec.Security.Symmetric;

/// <summary>
/// Vectorized ChaCha20 block function and keystream merge used by <see cref="ChaCha20"/>.
/// </summary>
/// <remarks>
/// <para>
/// Four keystream paths are provided and selected once per call by hardware capability, in the
/// same style as <see cref="Hashing.Keccak256"/>:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="XorEightBlocks"/> — eight blocks per pass (AVX2), one state word of
/// all eight blocks per register, so the rounds need no shuffles; XORed straight into the output.</description></item>
/// <item><description><see cref="KeyStreamTwoBlocks"/> — two blocks per pass, one per 128-bit lane
/// of a <see cref="Vector256{T}"/> (AVX2).</description></item>
/// <item><description><see cref="KeyStreamOneBlock"/> — one block held in four
/// <see cref="Vector128{T}"/> registers (SSE2 / AdvSimd).</description></item>
/// <item><description><see cref="KeyStreamScalar"/> — portable fallback over 16 locals, so the
/// round function stays free of span bounds checks.</description></item>
/// </list>
/// <para>
/// All four produce byte-identical output to RFC 8439 §2.3; the vector paths differ from the
/// scalar one only in how the same additions, XORs and rotations are scheduled.
/// </para>
/// </remarks>
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
internal static class ChaCha20Core
{
    /// <summary>Number of ChaCha20 rounds, as double rounds.</summary>
    private const int DoubleRounds = 10;

    /// <summary>True when <see cref="KeyStreamTwoBlocks"/> can be used on this machine.</summary>
    internal static bool TwoBlockPathSupported => Vector256.IsHardwareAccelerated;

    /// <summary>True when <see cref="XorEightBlocks"/> can be used on this machine.</summary>
    internal static bool EightBlockPathSupported => Avx2.IsSupported;

    #region Keystream — eight blocks (AVX2, word-sliced)

    /// <summary>
    /// XORs eight blocks of keystream, for block counters <paramref name="counter"/> to
    /// <paramref name="counter"/> + 7, into <paramref name="dst"/>: <c>dst[..512] = src[..512] ^ keystream</c>.
    /// The keystream never leaves registers. <c>state[12]</c> is not read: the caller advances it, checking for
    /// overflow, before calling, and checks <see cref="EightBlockPathSupported"/>.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void XorEightBlocks(
        scoped System.ReadOnlySpan<uint> state,
        uint counter,
        scoped System.ReadOnlySpan<byte> src,
        scoped System.Span<byte> dst)
    {
        // Register i holds word i of the eight blocks; only the counter word differs between them.
        Vector256<uint> x0 = Vector256.Create(state[0]), x1 = Vector256.Create(state[1]);
        Vector256<uint> x2 = Vector256.Create(state[2]), x3 = Vector256.Create(state[3]);
        Vector256<uint> x4 = Vector256.Create(state[4]), x5 = Vector256.Create(state[5]);
        Vector256<uint> x6 = Vector256.Create(state[6]), x7 = Vector256.Create(state[7]);
        Vector256<uint> x8 = Vector256.Create(state[8]), x9 = Vector256.Create(state[9]);
        Vector256<uint> x10 = Vector256.Create(state[10]), x11 = Vector256.Create(state[11]);
        Vector256<uint> x12 = Vector256.Create(counter) + Vector256.Create(0u, 1u, 2u, 3u, 4u, 5u, 6u, 7u);
        Vector256<uint> x13 = Vector256.Create(state[13]), x14 = Vector256.Create(state[14]);
        Vector256<uint> x15 = Vector256.Create(state[15]);
        Vector256<uint> counters = x12;

        for (int i = 0; i < DoubleRounds; i++)
        {
            // Column rounds.
            QuarterRound(ref x0, ref x4, ref x8, ref x12);
            QuarterRound(ref x1, ref x5, ref x9, ref x13);
            QuarterRound(ref x2, ref x6, ref x10, ref x14);
            QuarterRound(ref x3, ref x7, ref x11, ref x15);

            // Diagonal rounds.
            QuarterRound(ref x0, ref x5, ref x10, ref x15);
            QuarterRound(ref x1, ref x6, ref x11, ref x12);
            QuarterRound(ref x2, ref x7, ref x8, ref x13);
            QuarterRound(ref x3, ref x4, ref x9, ref x14);
        }

        x0 += Vector256.Create(state[0]); x1 += Vector256.Create(state[1]);
        x2 += Vector256.Create(state[2]); x3 += Vector256.Create(state[3]);
        x4 += Vector256.Create(state[4]); x5 += Vector256.Create(state[5]);
        x6 += Vector256.Create(state[6]); x7 += Vector256.Create(state[7]);
        x8 += Vector256.Create(state[8]); x9 += Vector256.Create(state[9]);
        x10 += Vector256.Create(state[10]); x11 += Vector256.Create(state[11]);
        x12 += counters; x13 += Vector256.Create(state[13]);
        x14 += Vector256.Create(state[14]); x15 += Vector256.Create(state[15]);

        // Words 0-7 of the eight blocks, then words 8-15: each transpose yields 32 bytes per block.
        XorTransposed(x0, x1, x2, x3, x4, x5, x6, x7, src, dst);
        XorTransposed(x8, x9, x10, x11, x12, x13, x14, x15, src[32..], dst[32..]);
    }

    /// <summary>
    /// Transposes eight registers of word-sliced state (register i = word i of blocks 0-7) into eight runs of
    /// eight consecutive words, one per block, and XORs run k into <c>[64k, 64k + 32)</c> of
    /// <paramref name="src"/>/<paramref name="dst"/>.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void XorTransposed(
        Vector256<uint> r0, Vector256<uint> r1, Vector256<uint> r2, Vector256<uint> r3,
        Vector256<uint> r4, Vector256<uint> r5, Vector256<uint> r6, Vector256<uint> r7,
        scoped System.ReadOnlySpan<byte> src,
        scoped System.Span<byte> dst)
    {
        // Within each 128-bit lane: interleave 32-bit words, then 64-bit pairs.
        Vector256<uint> t0 = Avx2.UnpackLow(r0, r1), t1 = Avx2.UnpackHigh(r0, r1);
        Vector256<uint> t2 = Avx2.UnpackLow(r2, r3), t3 = Avx2.UnpackHigh(r2, r3);
        Vector256<uint> t4 = Avx2.UnpackLow(r4, r5), t5 = Avx2.UnpackHigh(r4, r5);
        Vector256<uint> t6 = Avx2.UnpackLow(r6, r7), t7 = Avx2.UnpackHigh(r6, r7);

        Vector256<uint> u0 = Avx2.UnpackLow(t0.AsUInt64(), t2.AsUInt64()).AsUInt32();
        Vector256<uint> u1 = Avx2.UnpackHigh(t0.AsUInt64(), t2.AsUInt64()).AsUInt32();
        Vector256<uint> u2 = Avx2.UnpackLow(t1.AsUInt64(), t3.AsUInt64()).AsUInt32();
        Vector256<uint> u3 = Avx2.UnpackHigh(t1.AsUInt64(), t3.AsUInt64()).AsUInt32();
        Vector256<uint> u4 = Avx2.UnpackLow(t4.AsUInt64(), t6.AsUInt64()).AsUInt32();
        Vector256<uint> u5 = Avx2.UnpackHigh(t4.AsUInt64(), t6.AsUInt64()).AsUInt32();
        Vector256<uint> u6 = Avx2.UnpackLow(t5.AsUInt64(), t7.AsUInt64()).AsUInt32();
        Vector256<uint> u7 = Avx2.UnpackHigh(t5.AsUInt64(), t7.AsUInt64()).AsUInt32();

        // Lane 0 of u0-u3 holds blocks 0-3 (words 0-3), lane 1 blocks 4-7; u4-u7 hold words 4-7.
        XorRun(Avx2.Permute2x128(u0, u4, 0x20), src, dst, 0);
        XorRun(Avx2.Permute2x128(u1, u5, 0x20), src, dst, 1);
        XorRun(Avx2.Permute2x128(u2, u6, 0x20), src, dst, 2);
        XorRun(Avx2.Permute2x128(u3, u7, 0x20), src, dst, 3);
        XorRun(Avx2.Permute2x128(u0, u4, 0x31), src, dst, 4);
        XorRun(Avx2.Permute2x128(u1, u5, 0x31), src, dst, 5);
        XorRun(Avx2.Permute2x128(u2, u6, 0x31), src, dst, 6);
        XorRun(Avx2.Permute2x128(u3, u7, 0x31), src, dst, 7);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void XorRun(Vector256<uint> keystream, scoped System.ReadOnlySpan<byte> src, scoped System.Span<byte> dst, int block)
    {
        // AVX2 is x86 only, so the words are already little-endian.
        int offset = block * 64;
        (Vector256.Create(src.Slice(offset, 32)) ^ keystream.AsByte()).CopyTo(dst.Slice(offset, 32));
    }

    #endregion Keystream — eight blocks (AVX2, word-sliced)

    #region Keystream — two blocks (Vector256)

    /// <summary>
    /// Writes 128 bytes of keystream for block counters <c>state[12]</c> and <c>state[12] + 1</c>.
    /// </summary>
    /// <param name="state">The 16-word ChaCha20 state. Not modified.</param>
    /// <param name="dst">Destination of at least 128 bytes.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void KeyStreamTwoBlocks(
        scoped System.ReadOnlySpan<uint> state,
        scoped System.Span<byte> dst)
    {
        // Each 128-bit lane carries one block, so the lane-local shuffles below are the same
        // indices the one-block path uses, repeated for the upper lane.
        Vector256<uint> a0 = Broadcast128(state[..4]);
        Vector256<uint> b0 = Broadcast128(state[4..8]);
        Vector256<uint> c0 = Broadcast128(state[8..12]);

        // Only the counter word differs between the two lanes.
        Vector256<uint> d0 = Vector256.Create(
            state[12], state[13], state[14], state[15],
            state[12] + 1u, state[13], state[14], state[15]);

        Vector256<uint> a = a0, b = b0, c = c0, d = d0;

        for (int i = 0; i < DoubleRounds; i++)
        {
            QuarterRound(ref a, ref b, ref c, ref d);

            b = RotateLanes(b, 1);
            c = RotateLanes(c, 2);
            d = RotateLanes(d, 3);

            QuarterRound(ref a, ref b, ref c, ref d);

            b = RotateLanes(b, 3);
            c = RotateLanes(c, 2);
            d = RotateLanes(d, 1);
        }

        Store(a + a0, b + b0, c + c0, d + d0, dst);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Broadcast128(scoped System.ReadOnlySpan<uint> words)
    {
        Vector128<uint> lane = Vector128.Create(words);
        return Vector256.Create(lane, lane);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(
        ref Vector256<uint> a,
        ref Vector256<uint> b,
        ref Vector256<uint> c,
        ref Vector256<uint> d)
    {
        a += b; d = RotateLeft(d ^ a, 16);
        c += d; b = RotateLeft(b ^ c, 12);
        a += b; d = RotateLeft(d ^ a, 8);
        c += d; b = RotateLeft(b ^ c, 7);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> RotateLeft(Vector256<uint> v, byte bits)
        => Vector256.ShiftLeft(v, bits) | Vector256.ShiftRightLogical(v, 32 - bits);

    /// <summary>Rotates each 128-bit lane left by <paramref name="words"/> 32-bit elements.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> RotateLanes(Vector256<uint> v, byte words) => words switch
    {
        1 => Vector256.Shuffle(v, Vector256.Create(1u, 2u, 3u, 0u, 5u, 6u, 7u, 4u)),
        2 => Vector256.Shuffle(v, Vector256.Create(2u, 3u, 0u, 1u, 6u, 7u, 4u, 5u)),
        _ => Vector256.Shuffle(v, Vector256.Create(3u, 0u, 1u, 2u, 7u, 4u, 5u, 6u)),
    };

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void Store(
        Vector256<uint> a,
        Vector256<uint> b,
        Vector256<uint> c,
        Vector256<uint> d,
        scoped System.Span<byte> dst)
    {
        // Lane 0 is the first block's four rows; lane 1 is the second block's.
        WriteRow(a.GetLower(), dst[..16]);
        WriteRow(b.GetLower(), dst[16..32]);
        WriteRow(c.GetLower(), dst[32..48]);
        WriteRow(d.GetLower(), dst[48..64]);

        WriteRow(a.GetUpper(), dst[64..80]);
        WriteRow(b.GetUpper(), dst[80..96]);
        WriteRow(c.GetUpper(), dst[96..112]);
        WriteRow(d.GetUpper(), dst[112..128]);
    }

    #endregion Keystream — two blocks (Vector256)

    #region Keystream — one block (Vector128)

    /// <summary>
    /// Writes 64 bytes of keystream for block counter <c>state[12]</c>.
    /// </summary>
    /// <param name="state">The 16-word ChaCha20 state. Not modified.</param>
    /// <param name="dst">Destination of at least 64 bytes.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void KeyStreamOneBlock(
        scoped System.ReadOnlySpan<uint> state,
        scoped System.Span<byte> dst)
    {
        Vector128<uint> a0 = Vector128.Create(state[..4]);
        Vector128<uint> b0 = Vector128.Create(state[4..8]);
        Vector128<uint> c0 = Vector128.Create(state[8..12]);
        Vector128<uint> d0 = Vector128.Create(state[12..16]);

        Vector128<uint> a = a0, b = b0, c = c0, d = d0;

        for (int i = 0; i < DoubleRounds; i++)
        {
            QuarterRound(ref a, ref b, ref c, ref d);

            b = RotateLanes(b, 1);
            c = RotateLanes(c, 2);
            d = RotateLanes(d, 3);

            QuarterRound(ref a, ref b, ref c, ref d);

            b = RotateLanes(b, 3);
            c = RotateLanes(c, 2);
            d = RotateLanes(d, 1);
        }

        WriteRow(a + a0, dst[..16]);
        WriteRow(b + b0, dst[16..32]);
        WriteRow(c + c0, dst[32..48]);
        WriteRow(d + d0, dst[48..64]);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(
        ref Vector128<uint> a,
        ref Vector128<uint> b,
        ref Vector128<uint> c,
        ref Vector128<uint> d)
    {
        a += b; d = RotateLeft(d ^ a, 16);
        c += d; b = RotateLeft(b ^ c, 12);
        a += b; d = RotateLeft(d ^ a, 8);
        c += d; b = RotateLeft(b ^ c, 7);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RotateLeft(Vector128<uint> v, byte bits)
        => Vector128.ShiftLeft(v, bits) | Vector128.ShiftRightLogical(v, 32 - bits);

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RotateLanes(Vector128<uint> v, byte words) => words switch
    {
        1 => Vector128.Shuffle(v, Vector128.Create(1u, 2u, 3u, 0u)),
        2 => Vector128.Shuffle(v, Vector128.Create(2u, 3u, 0u, 1u)),
        _ => Vector128.Shuffle(v, Vector128.Create(3u, 0u, 1u, 2u)),
    };

    /// <summary>Writes one state row as four little-endian words.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void WriteRow(Vector128<uint> row, scoped System.Span<byte> dst)
    {
        if (System.BitConverter.IsLittleEndian)
        {
            row.CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst));
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dst[(4 * i)..], row[i]);
        }
    }

    #endregion Keystream — one block (Vector128)

    #region Keystream — scalar fallback

    /// <summary>
    /// Writes 64 bytes of keystream for block counter <c>state[12]</c> without using vectors.
    /// </summary>
    /// <param name="state">The 16-word ChaCha20 state. Not modified.</param>
    /// <param name="dst">Destination of at least 64 bytes.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void KeyStreamScalar(
        scoped System.ReadOnlySpan<uint> state,
        scoped System.Span<byte> dst)
    {
        uint s0 = state[0], s1 = state[1], s2 = state[2], s3 = state[3];
        uint s4 = state[4], s5 = state[5], s6 = state[6], s7 = state[7];
        uint s8 = state[8], s9 = state[9], s10 = state[10], s11 = state[11];
        uint s12 = state[12], s13 = state[13], s14 = state[14], s15 = state[15];

        uint x0 = s0, x1 = s1, x2 = s2, x3 = s3;
        uint x4 = s4, x5 = s5, x6 = s6, x7 = s7;
        uint x8 = s8, x9 = s9, x10 = s10, x11 = s11;
        uint x12 = s12, x13 = s13, x14 = s14, x15 = s15;

        for (int i = 0; i < DoubleRounds; i++)
        {
            // Column rounds.
            QuarterRound(ref x0, ref x4, ref x8, ref x12);
            QuarterRound(ref x1, ref x5, ref x9, ref x13);
            QuarterRound(ref x2, ref x6, ref x10, ref x14);
            QuarterRound(ref x3, ref x7, ref x11, ref x15);

            // Diagonal rounds.
            QuarterRound(ref x0, ref x5, ref x10, ref x15);
            QuarterRound(ref x1, ref x6, ref x11, ref x12);
            QuarterRound(ref x2, ref x7, ref x8, ref x13);
            QuarterRound(ref x3, ref x4, ref x9, ref x14);
        }

        System.Span<uint> outWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst[..64]);

        Write(outWords, 0, x0 + s0); Write(outWords, 1, x1 + s1);
        Write(outWords, 2, x2 + s2); Write(outWords, 3, x3 + s3);
        Write(outWords, 4, x4 + s4); Write(outWords, 5, x5 + s5);
        Write(outWords, 6, x6 + s6); Write(outWords, 7, x7 + s7);
        Write(outWords, 8, x8 + s8); Write(outWords, 9, x9 + s9);
        Write(outWords, 10, x10 + s10); Write(outWords, 11, x11 + s11);
        Write(outWords, 12, x12 + s12); Write(outWords, 13, x13 + s13);
        Write(outWords, 14, x14 + s14); Write(outWords, 15, x15 + s15);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void Write(scoped System.Span<uint> dst, int index, uint value)
        => dst[index] = System.BitConverter.IsLittleEndian
            ? value
            : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d = System.Numerics.BitOperations.RotateLeft(d ^ a, 16);
        c += d; b = System.Numerics.BitOperations.RotateLeft(b ^ c, 12);
        a += b; d = System.Numerics.BitOperations.RotateLeft(d ^ a, 8);
        c += d; b = System.Numerics.BitOperations.RotateLeft(b ^ c, 7);
    }

    #endregion Keystream — scalar fallback

    #region Keystream merge

    /// <summary>
    /// Writes <c>src XOR keystream</c> into <paramref name="dst"/> for <paramref name="count"/> bytes.
    /// </summary>
    /// <param name="src">The source bytes.</param>
    /// <param name="keystream">The keystream bytes.</param>
    /// <param name="dst">The destination bytes.</param>
    /// <param name="count">How many bytes to merge.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void Xor(
        scoped System.ReadOnlySpan<byte> src,
        scoped System.ReadOnlySpan<byte> keystream,
        scoped System.Span<byte> dst,
        int count)
    {
        int offset = 0;

        if (Vector256.IsHardwareAccelerated)
        {
            for (; offset + Vector256<byte>.Count <= count; offset += Vector256<byte>.Count)
            {
                (Vector256.LoadUnsafe(in src[offset]) ^ Vector256.LoadUnsafe(in keystream[offset]))
                    .StoreUnsafe(ref dst[offset]);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; offset + Vector128<byte>.Count <= count; offset += Vector128<byte>.Count)
            {
                (Vector128.LoadUnsafe(in src[offset]) ^ Vector128.LoadUnsafe(in keystream[offset]))
                    .StoreUnsafe(ref dst[offset]);
            }
        }

        for (; offset < count; offset++)
        {
            dst[offset] = (byte)(src[offset] ^ keystream[offset]);
        }
    }

    #endregion Keystream merge
}
