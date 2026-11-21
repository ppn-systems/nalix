// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nalix.Codec.Security.Hashing;

/// <summary>
/// Four-way AVX2 block loop for <see cref="Poly1305"/>: the same 26-bit-limb arithmetic as the scalar loop,
/// run on four accumulators at once.
/// </summary>
/// <remarks>
/// <para>
/// The sequential recurrence <c>h = (h + m) · r</c> over four blocks expands to
/// <c>(h + m₀)·r⁴ + m₁·r³ + m₂·r² + m₃·r</c>. Each 64-bit lane carries one accumulator: every group of four
/// blocks adds one block per lane and multiplies every lane by r⁴, except the last group, where each lane is
/// multiplied by the power that brings its block to the end of the sequence. The lanes are then summed. The
/// result is the value the scalar loop computes, partially reduced in the same shape.
/// </para>
/// <para>
/// Products use <c>vpmuludq</c> (32×32→64 bits): limbs stay below 2²⁸ and the folded key limbs below 2³⁰, so
/// each sum of five products stays below 2⁶⁰.
/// </para>
/// </remarks>
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
internal static class Poly1305Core
{
    private const int LimbBits = 26;
    private const uint LimbMask = (1u << LimbBits) - 1u;
    private const int GroupBytes = 64;

    /// <summary>True when <see cref="AbsorbGroups"/> can be used on this machine.</summary>
    internal static bool FourWayPathSupported => Avx2.IsSupported;

    /// <summary>
    /// Runs shorter than this stay on the scalar loop: computing r², r³ and r⁴ costs about three blocks.
    /// </summary>
    internal const int FourWayMinimumBytes = 256;

    /// <summary>
    /// Absorbs <paramref name="groups"/>, a whole number of 64-byte groups of full blocks, into
    /// <paramref name="h"/>.
    /// </summary>
    /// <param name="h">The five accumulator limbs, partially reduced as the scalar loop leaves them; updated.</param>
    /// <param name="r">The five limbs of the clamped key <c>r</c>.</param>
    /// <param name="groups">A non-empty whole number of 64-byte groups.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void AbsorbGroups(
        scoped System.Span<uint> h,
        scoped System.ReadOnlySpan<uint> r,
        scoped System.ReadOnlySpan<byte> groups)
    {
        System.Diagnostics.Debug.Assert(groups.Length > 0 && groups.Length % GroupBytes == 0);

        // Powers of r, partially reduced (each limb below 2²⁷).
        System.Span<uint> r2 = stackalloc uint[5];
        System.Span<uint> r3 = stackalloc uint[5];
        System.Span<uint> r4 = stackalloc uint[5];
        Multiply(r, r, r2);
        Multiply(r2, r, r3);
        Multiply(r2, r2, r4);

        // Lanes hold blocks 0, 2, 1, 3 of a group (see LoadGroup); the last group multiplies each lane by the
        // power that brings its block to the end: r⁴, r², r³, r.
        Vector256<ulong> k0 = Vector256.Create((ulong)r4[0]), k1 = Vector256.Create((ulong)r4[1]);
        Vector256<ulong> k2 = Vector256.Create((ulong)r4[2]), k3 = Vector256.Create((ulong)r4[3]);
        Vector256<ulong> k4 = Vector256.Create<ulong>(r4[4]);

        Vector256<ulong> h0 = Vector256.Create(h[0], 0UL, 0UL, 0UL), h1 = Vector256.Create(h[1], 0UL, 0UL, 0UL);
        Vector256<ulong> h2 = Vector256.Create(h[2], 0UL, 0UL, 0UL), h3 = Vector256.Create(h[3], 0UL, 0UL, 0UL);
        Vector256<ulong> h4 = Vector256.Create(h[4], 0UL, 0UL, 0UL);

        int last = groups.Length - GroupBytes;
        for (int offset = 0; offset < last; offset += GroupBytes)
        {
            AddGroup(groups.Slice(offset, GroupBytes), ref h0, ref h1, ref h2, ref h3, ref h4);
            MultiplyLanes(ref h0, ref h1, ref h2, ref h3, ref h4, k0, k1, k2, k3, k4);
        }

        AddGroup(groups.Slice(last, GroupBytes), ref h0, ref h1, ref h2, ref h3, ref h4);
        MultiplyLanes(
            ref h0, ref h1, ref h2, ref h3, ref h4,
            Vector256.Create((ulong)r4[0], r2[0], r3[0], r[0]),
            Vector256.Create((ulong)r4[1], r2[1], r3[1], r[1]),
            Vector256.Create((ulong)r4[2], r2[2], r3[2], r[2]),
            Vector256.Create((ulong)r4[3], r2[3], r3[3], r[3]),
            Vector256.Create((ulong)r4[4], r2[4], r3[4], r[4]));

        // Sum the lanes (each limb below 2²⁷, the sum below 2²⁹) and carry back to the scalar shape:
        // limbs below 2²⁶ except limb 1, which may hold one small carry.
        ulong a0 = Vector256.Sum(h0), a1 = Vector256.Sum(h1), a2 = Vector256.Sum(h2);
        ulong a3 = Vector256.Sum(h3), a4 = Vector256.Sum(h4);

        a1 += a0 >> LimbBits;
        a0 &= LimbMask;
        a2 += a1 >> LimbBits;
        a1 &= LimbMask;
        a3 += a2 >> LimbBits;
        a2 &= LimbMask;
        a4 += a3 >> LimbBits;
        a3 &= LimbMask;
        a0 += (a4 >> LimbBits) * 5;
        a4 &= LimbMask;
        a1 += a0 >> LimbBits;
        a0 &= LimbMask;

        h[0] = (uint)a0;
        h[1] = (uint)a1;
        h[2] = (uint)a2;
        h[3] = (uint)a3;
        h[4] = (uint)a4;

        r2.Clear();
        r3.Clear();
        r4.Clear();
    }

    /// <summary>
    /// Splits the four blocks of <paramref name="group"/> into 26-bit limbs, one block per lane in the order
    /// 0, 2, 1, 3, and adds them (with the 2¹²⁸ bit of a full block) to the accumulators.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void AddGroup(
        scoped System.ReadOnlySpan<byte> group,
        ref Vector256<ulong> h0, ref Vector256<ulong> h1, ref Vector256<ulong> h2,
        ref Vector256<ulong> h3, ref Vector256<ulong> h4)
    {
        // a = blocks 0 and 1, b = blocks 2 and 3, as 64-bit words (AVX2 is x86 only: little-endian).
        Vector256<ulong> a = Vector256.Create(group[..32]).AsUInt64();
        Vector256<ulong> b = Vector256.Create(group.Slice(32, 32)).AsUInt64();

        // Per 128-bit lane: low words (bytes 0-7) and high words (bytes 8-15) of blocks 0, 2 | 1, 3.
        Vector256<ulong> lo = Avx2.UnpackLow(a, b);
        Vector256<ulong> hi = Avx2.UnpackHigh(a, b);

        Vector256<ulong> mask = Vector256.Create((ulong)LimbMask);
        h0 += lo & mask;
        h1 += Vector256.ShiftRightLogical(lo, 26) & mask;
        h2 += (Vector256.ShiftRightLogical(lo, 52) | Vector256.ShiftLeft(hi, 12)) & mask;
        h3 += Vector256.ShiftRightLogical(hi, 14) & mask;
        h4 += Vector256.ShiftRightLogical(hi, 40) | Vector256.Create(1UL << 24);
    }

    /// <summary>
    /// <c>h = h · k mod 2¹³⁰ − 5</c> per lane, partially reduced with one carry pass.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void MultiplyLanes(
        ref Vector256<ulong> h0, ref Vector256<ulong> h1, ref Vector256<ulong> h2,
        ref Vector256<ulong> h3, ref Vector256<ulong> h4,
        Vector256<ulong> k0, Vector256<ulong> k1, Vector256<ulong> k2, Vector256<ulong> k3, Vector256<ulong> k4)
    {
        // Limbs that land at or above 2¹³⁰ fold back times five.
        Vector256<ulong> s1 = k1 + Vector256.ShiftLeft(k1, 2);
        Vector256<ulong> s2 = k2 + Vector256.ShiftLeft(k2, 2);
        Vector256<ulong> s3 = k3 + Vector256.ShiftLeft(k3, 2);
        Vector256<ulong> s4 = k4 + Vector256.ShiftLeft(k4, 2);

        Vector256<ulong> d0 = Mul(h0, k0) + Mul(h1, s4) + Mul(h2, s3) + Mul(h3, s2) + Mul(h4, s1);
        Vector256<ulong> d1 = Mul(h0, k1) + Mul(h1, k0) + Mul(h2, s4) + Mul(h3, s3) + Mul(h4, s2);
        Vector256<ulong> d2 = Mul(h0, k2) + Mul(h1, k1) + Mul(h2, k0) + Mul(h3, s4) + Mul(h4, s3);
        Vector256<ulong> d3 = Mul(h0, k3) + Mul(h1, k2) + Mul(h2, k1) + Mul(h3, k0) + Mul(h4, s4);
        Vector256<ulong> d4 = Mul(h0, k4) + Mul(h1, k3) + Mul(h2, k2) + Mul(h3, k1) + Mul(h4, k0);

        Vector256<ulong> mask = Vector256.Create((ulong)LimbMask);

        d1 += Vector256.ShiftRightLogical(d0, LimbBits);
        h0 = d0 & mask;
        d2 += Vector256.ShiftRightLogical(d1, LimbBits);
        h1 = d1 & mask;
        d3 += Vector256.ShiftRightLogical(d2, LimbBits);
        h2 = d2 & mask;
        d4 += Vector256.ShiftRightLogical(d3, LimbBits);
        h3 = d3 & mask;

        Vector256<ulong> c = Vector256.ShiftRightLogical(d4, LimbBits);
        h4 = d4 & mask;
        h0 += c + Vector256.ShiftLeft(c, 2);
        h1 += Vector256.ShiftRightLogical(h0, LimbBits);
        h0 &= mask;
    }

    /// <summary>Products of the low 32 bits of each lane (<c>vpmuludq</c>).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> Mul(Vector256<ulong> a, Vector256<ulong> b) => Avx2.Multiply(a.AsUInt32(), b.AsUInt32());

    /// <summary>
    /// <c>result = a · b mod 2¹³⁰ − 5</c> on 26-bit limbs, partially reduced (every limb below 2²⁷).
    /// </summary>
    private static void Multiply(scoped System.ReadOnlySpan<uint> a, scoped System.ReadOnlySpan<uint> b, scoped System.Span<uint> result)
    {
        ulong a0 = a[0], a1 = a[1], a2 = a[2], a3 = a[3], a4 = a[4];
        ulong b0 = b[0], b1 = b[1], b2 = b[2], b3 = b[3], b4 = b[4];
        ulong s1 = b1 * 5, s2 = b2 * 5, s3 = b3 * 5, s4 = b4 * 5;

        ulong d0 = (a0 * b0) + (a1 * s4) + (a2 * s3) + (a3 * s2) + (a4 * s1);
        ulong d1 = (a0 * b1) + (a1 * b0) + (a2 * s4) + (a3 * s3) + (a4 * s2);
        ulong d2 = (a0 * b2) + (a1 * b1) + (a2 * b0) + (a3 * s4) + (a4 * s3);
        ulong d3 = (a0 * b3) + (a1 * b2) + (a2 * b1) + (a3 * b0) + (a4 * s4);
        ulong d4 = (a0 * b4) + (a1 * b3) + (a2 * b2) + (a3 * b1) + (a4 * b0);

        d1 += d0 >> LimbBits;
        d2 += d1 >> LimbBits;
        d3 += d2 >> LimbBits;
        d4 += d3 >> LimbBits;

        ulong r0 = (d0 & LimbMask) + ((d4 >> LimbBits) * 5);
        result[0] = (uint)(r0 & LimbMask);
        result[1] = (uint)((d1 & LimbMask) + (r0 >> LimbBits));
        result[2] = (uint)(d2 & LimbMask);
        result[3] = (uint)(d3 & LimbMask);
        result[4] = (uint)(d4 & LimbMask);
    }
}
