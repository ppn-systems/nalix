// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Codec.Security.Asymmetric;

/// <summary>
/// Represents an element of the field GF(2^255 - 19).
/// An element t, entries t[0]...t[9], represents the integer
/// t[0]+2^26·t[1]+2^51·t[2]+2^77·t[3]+2^102·t[4]+...+2^230·t[9].
/// </summary>
/// <remarks>
/// Declared as a <see langword="struct"/> so that every arithmetic operation
/// returns a value type — no heap allocation, no GC pressure.
/// All carry temporaries are scalar locals rather than <c>new int[10]</c> arrays.
/// </remarks>
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
internal struct FieldElement
{
    /// <summary>
    /// 10 limbs, 26/25-bit alternating radix-2^25.5 representation.
    /// Stored inline — no inner array allocation.
    /// </summary>
    internal int E0, E1, E2, E3, E4, E5, E6, E7, E8, E9;

    /// <summary>
    /// ── Indexer (used by legacy call-sites) ──────────────────────────────────
    /// </summary>
    /// <param name="i"></param>
    /// <returns></returns>
    public int this[int i]
    {
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        readonly get => i switch
        {
            0 => E0,
            1 => E1,
            2 => E2,
            3 => E3,
            4 => E4,
            5 => E5,
            6 => E6,
            7 => E7,
            8 => E8,
            9 => E9,
            _ => throw new System.ArgumentOutOfRangeException(nameof(i))
        };

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set
        {
            switch (i)
            {
                case 0: E0 = value; break;
                case 1: E1 = value; break;
                case 2: E2 = value; break;
                case 3: E3 = value; break;
                case 4: E4 = value; break;
                case 5: E5 = value; break;
                case 6: E6 = value; break;
                case 7: E7 = value; break;
                case 8: E8 = value; break;
                case 9: E9 = value; break;
                default: throw new System.ArgumentOutOfRangeException(nameof(i));
            }
        }
    }

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>feFromBytes — reads a 32-byte little-endian field element.</summary>
    /// <param name="src"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public FieldElement(System.ReadOnlySpan<byte> src)
    {
        // Read directly from span — no SubArray temp allocation.
        long h0 = Load4(src, 0);
        long h1 = Load3(src, 4) << 6;
        long h2 = Load3(src, 7) << 5;
        long h3 = Load3(src, 10) << 3;
        long h4 = Load3(src, 13) << 2;
        long h5 = Load4(src, 16);
        long h6 = Load3(src, 20) << 7;
        long h7 = Load3(src, 23) << 5;
        long h8 = Load3(src, 26) << 4;
        long h9 = (Load3(src, 29) & 0x7fffff) << 2;

        // Carry reduction — all scalar locals, zero heap.
        long c9 = (h9 + (1 << 24)) >> 25; h0 += c9 * 19; h9 -= c9 << 25;
        long c1 = (h1 + (1 << 24)) >> 25; h2 += c1; h1 -= c1 << 25;
        long c3 = (h3 + (1 << 24)) >> 25; h4 += c3; h3 -= c3 << 25;
        long c5 = (h5 + (1 << 24)) >> 25; h6 += c5; h5 -= c5 << 25;
        long c7 = (h7 + (1 << 24)) >> 25; h8 += c7; h7 -= c7 << 25;
        long c0 = (h0 + (1 << 25)) >> 26; h1 += c0; h0 -= c0 << 26;
        long c2 = (h2 + (1 << 25)) >> 26; h3 += c2; h2 -= c2 << 26;
        long c4 = (h4 + (1 << 25)) >> 26; h5 += c4; h4 -= c4 << 26;
        long c6 = (h6 + (1 << 25)) >> 26; h7 += c6; h6 -= c6 << 26;
        long c8 = (h8 + (1 << 25)) >> 26; h9 += c8; h8 -= c8 << 26;

        E0 = (int)h0; E1 = (int)h1;
        E2 = (int)h2; E3 = (int)h3;
        E4 = (int)h4; E5 = (int)h5;
        E6 = (int)h6; E7 = (int)h7;
        E8 = (int)h8; E9 = (int)h9;
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Sets this element to 1 (the multiplicative identity).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void One()
    {
        E0 = 1;
        E1 = E2 = E3 = E4 = E5 = E6 = E7 = E8 = E9 = 0;
    }

    // ── Serialization ────────────────────────────────────────────────────────

    /// <summary>
    /// feToBytes — writes the canonical 32-byte little-endian encoding into
    /// <paramref name="output"/>. No heap allocation.
    /// </summary>
    /// <param name="output"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public readonly void ToBytes(System.Span<byte> output)
    {
        // Work on local copies so we don't mutate the struct's own fields.
        int h0 = E0, h1 = E1, h2 = E2, h3 = E3, h4 = E4;
        int h5 = E5, h6 = E6, h7 = E7, h8 = E8, h9 = E9;

        int q = ((19 * h9) + (1 << 24)) >> 25;
        q = (h0 + q) >> 26; q = (h1 + q) >> 25; q = (h2 + q) >> 26;
        q = (h3 + q) >> 25; q = (h4 + q) >> 26; q = (h5 + q) >> 25;
        q = (h6 + q) >> 26; q = (h7 + q) >> 25; q = (h8 + q) >> 26;
        q = (h9 + q) >> 25;

        h0 += 19 * q;

        // Carry propagation — scalar locals.
        int c0 = h0 >> 26; h1 += c0; h0 -= c0 << 26;
        int c1 = h1 >> 25; h2 += c1; h1 -= c1 << 25;
        int c2 = h2 >> 26; h3 += c2; h2 -= c2 << 26;
        int c3 = h3 >> 25; h4 += c3; h3 -= c3 << 25;
        int c4 = h4 >> 26; h5 += c4; h4 -= c4 << 26;
        int c5 = h5 >> 25; h6 += c5; h5 -= c5 << 25;
        int c6 = h6 >> 26; h7 += c6; h6 -= c6 << 26;
        int c7 = h7 >> 25; h8 += c7; h7 -= c7 << 25;
        int c8 = h8 >> 26; h9 += c8; h8 -= c8 << 26;
        int c9 = h9 >> 25; h9 -= c9 << 25;

        output[0] = (byte)(h0 >> 0);
        output[1] = (byte)(h0 >> 8);
        output[2] = (byte)(h0 >> 16);
        output[3] = (byte)((h0 >> 24) | (h1 << 2));
        output[4] = (byte)(h1 >> 6);
        output[5] = (byte)(h1 >> 14);
        output[6] = (byte)((h1 >> 22) | (h2 << 3));
        output[7] = (byte)(h2 >> 5);
        output[8] = (byte)(h2 >> 13);
        output[9] = (byte)((h2 >> 21) | (h3 << 5));
        output[10] = (byte)(h3 >> 3);
        output[11] = (byte)(h3 >> 11);
        output[12] = (byte)((h3 >> 19) | (h4 << 6));
        output[13] = (byte)(h4 >> 2);
        output[14] = (byte)(h4 >> 10);
        output[15] = (byte)(h4 >> 18);
        output[16] = (byte)(h5 >> 0);
        output[17] = (byte)(h5 >> 8);
        output[18] = (byte)(h5 >> 16);
        output[19] = (byte)((h5 >> 24) | (h6 << 1));
        output[20] = (byte)(h6 >> 7);
        output[21] = (byte)(h6 >> 15);
        output[22] = (byte)((h6 >> 23) | (h7 << 3));
        output[23] = (byte)(h7 >> 5);
        output[24] = (byte)(h7 >> 13);
        output[25] = (byte)((h7 >> 21) | (h8 << 4));
        output[26] = (byte)(h8 >> 4);
        output[27] = (byte)(h8 >> 12);
        output[28] = (byte)((h8 >> 20) | (h9 << 6));
        output[29] = (byte)(h9 >> 2);
        output[30] = (byte)(h9 >> 10);
        output[31] = (byte)(h9 >> 18);
    }

    // ── Arithmetic operators ──────────────────────────────────────────────────

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static FieldElement operator +(in FieldElement f, in FieldElement g) => new()
    {
        E0 = f.E0 + g.E0,
        E1 = f.E1 + g.E1,
        E2 = f.E2 + g.E2,
        E3 = f.E3 + g.E3,
        E4 = f.E4 + g.E4,
        E5 = f.E5 + g.E5,
        E6 = f.E6 + g.E6,
        E7 = f.E7 + g.E7,
        E8 = f.E8 + g.E8,
        E9 = f.E9 + g.E9
    };

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static FieldElement operator -(in FieldElement f, in FieldElement g) => new()
    {
        E0 = f.E0 - g.E0,
        E1 = f.E1 - g.E1,
        E2 = f.E2 - g.E2,
        E3 = f.E3 - g.E3,
        E4 = f.E4 - g.E4,
        E5 = f.E5 - g.E5,
        E6 = f.E6 - g.E6,
        E7 = f.E7 - g.E7,
        E8 = f.E8 - g.E8,
        E9 = f.E9 - g.E9
    };

    // ── Multiply ─────────────────────────────────────────────────────────────

    /// <summary>h = this * g  (schoolbook multiplication mod p)</summary>
    /// <param name="g"></param>
    /// <param name="result"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining |
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public readonly void Multiply(in FieldElement g, out FieldElement result)
    {
        int f0 = E0, f1 = E1, f2 = E2, f3 = E3, f4 = E4;
        int f5 = E5, f6 = E6, f7 = E7, f8 = E8, f9 = E9;
        int g0 = g.E0, g1 = g.E1, g2 = g.E2, g3 = g.E3, g4 = g.E4;
        int g5 = g.E5, g6 = g.E6, g7 = g.E7, g8 = g.E8, g9 = g.E9;

        int f7_2 = 2 * f7, f9_2 = 2 * f9;
        int f1_2 = 2 * f1, f3_2 = 2 * f3, f5_2 = 2 * f5;
        int g1_19 = 19 * g1, g2_19 = 19 * g2, g3_19 = 19 * g3;
        int g4_19 = 19 * g4, g5_19 = 19 * g5, g6_19 = 19 * g6;
        int g7_19 = 19 * g7, g8_19 = 19 * g8, g9_19 = 19 * g9;

        long h9 = ((long)f0 * g9) + ((long)f1 * g8) + ((long)f2 * g7) + ((long)f3 * g6) + ((long)f4 * g5) + ((long)f5 * g4) + ((long)f6 * g3) + ((long)f7 * g2) + ((long)f8 * g1) + ((long)f9 * g0);
        long h8 = ((long)f0 * g8) + ((long)f1_2 * g7) + ((long)f2 * g6) + ((long)f3_2 * g5) + ((long)f4 * g4) + ((long)f5_2 * g3) + ((long)f6 * g2) + ((long)f7_2 * g1) + ((long)f8 * g0) + ((long)f9_2 * g9_19);
        long h7 = ((long)f0 * g7) + ((long)f1 * g6) + ((long)f2 * g5) + ((long)f3 * g4) + ((long)f4 * g3) + ((long)f5 * g2) + ((long)f6 * g1) + ((long)f7 * g0) + ((long)f8 * g9_19) + ((long)f9 * g8_19);
        long h6 = ((long)f0 * g6) + ((long)f1_2 * g5) + ((long)f2 * g4) + ((long)f3_2 * g3) + ((long)f4 * g2) + ((long)f5_2 * g1) + ((long)f6 * g0) + ((long)f7_2 * g9_19) + ((long)f8 * g8_19) + ((long)f9_2 * g7_19);
        long h5 = ((long)f0 * g5) + ((long)f1 * g4) + ((long)f2 * g3) + ((long)f3 * g2) + ((long)f4 * g1) + ((long)f5 * g0) + ((long)f6 * g9_19) + ((long)f7 * g8_19) + ((long)f8 * g7_19) + ((long)f9 * g6_19);
        long h4 = ((long)f0 * g4) + ((long)f1_2 * g3) + ((long)f2 * g2) + ((long)f3_2 * g1) + ((long)f4 * g0) + ((long)f5_2 * g9_19) + ((long)f6 * g8_19) + ((long)f7_2 * g7_19) + ((long)f8 * g6_19) + ((long)f9_2 * g5_19);
        long h3 = ((long)f0 * g3) + ((long)f1 * g2) + ((long)f2 * g1) + ((long)f3 * g0) + ((long)f4 * g9_19) + ((long)f5 * g8_19) + ((long)f6 * g7_19) + ((long)f7 * g6_19) + ((long)f8 * g5_19) + ((long)f9 * g4_19);
        long h2 = ((long)f0 * g2) + ((long)f1_2 * g1) + ((long)f2 * g0) + ((long)f3_2 * g9_19) + ((long)f4 * g8_19) + ((long)f5_2 * g7_19) + ((long)f6 * g6_19) + ((long)f7_2 * g5_19) + ((long)f8 * g4_19) + ((long)f9_2 * g3_19);
        long h1 = ((long)f0 * g1) + ((long)f1 * g0) + ((long)f2 * g9_19) + ((long)f3 * g8_19) + ((long)f4 * g7_19) + ((long)f5 * g6_19) + ((long)f6 * g5_19) + ((long)f7 * g4_19) + ((long)f8 * g3_19) + ((long)f9 * g2_19);
        long h0 = ((long)f0 * g0) + ((long)f1_2 * g9_19) + ((long)f2 * g8_19) + ((long)f3_2 * g7_19) + ((long)f4 * g6_19) + ((long)f5_2 * g5_19) + ((long)f6 * g4_19) + ((long)f7_2 * g3_19) + ((long)f8 * g2_19) + ((long)f9_2 * g1_19);

        ReduceCarry(h0, h1, h2, h3, h4, h5, h6, h7, h8, h9, out result);
    }

    // ── Square ───────────────────────────────────────────────────────────────

    /// <summary>h = this²</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining |
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public readonly void Square(out FieldElement result)
    {
        int f0 = E0, f1 = E1, f2 = E2, f3 = E3, f4 = E4;
        int f5 = E5, f6 = E6, f7 = E7, f8 = E8, f9 = E9;

        int f8_19 = 19 * f8, f9_38 = 38 * f9;
        int f5_38 = 38 * f5, f6_19 = 19 * f6, f7_38 = 38 * f7;
        int f0_2 = 2 * f0, f1_2 = 2 * f1, f2_2 = 2 * f2, f3_2 = 2 * f3;
        int f4_2 = 2 * f4, f5_2 = 2 * f5, f6_2 = 2 * f6, f7_2 = 2 * f7;

        long h0 = ((long)f0 * f0) + ((long)f1_2 * f9_38) + ((long)f2_2 * f8_19) + ((long)f3_2 * f7_38) + ((long)f4_2 * f6_19) + ((long)f5 * f5_38);
        long h1 = ((long)f0_2 * f1) + ((long)f2 * f9_38) + ((long)f3_2 * f8_19) + ((long)f4 * f7_38) + ((long)f5_2 * f6_19);
        long h2 = ((long)f0_2 * f2) + ((long)f1_2 * f1) + ((long)f3_2 * f9_38) + ((long)f4_2 * f8_19) + ((long)f5_2 * f7_38) + ((long)f6 * f6_19);
        long h3 = ((long)f0_2 * f3) + ((long)f1_2 * f2) + ((long)f4 * f9_38) + ((long)f5_2 * f8_19) + ((long)f6 * f7_38);
        long h4 = ((long)f0_2 * f4) + ((long)f1_2 * f3_2) + ((long)f2 * f2) + ((long)f5_2 * f9_38) + ((long)f6_2 * f8_19) + ((long)f7 * f7_38);
        long h5 = ((long)f0_2 * f5) + ((long)f1_2 * f4) + ((long)f2_2 * f3) + ((long)f6 * f9_38) + ((long)f7_2 * f8_19);
        long h6 = ((long)f0_2 * f6) + ((long)f1_2 * f5_2) + ((long)f2_2 * f4) + ((long)f3_2 * f3) + ((long)f7_2 * f9_38) + ((long)f8 * f8_19);
        long h7 = ((long)f0_2 * f7) + ((long)f1_2 * f6) + ((long)f2_2 * f5) + ((long)f3_2 * f4) + ((long)f8 * f9_38);
        long h8 = ((long)f0_2 * f8) + ((long)f1_2 * f7_2) + ((long)f2_2 * f6) + ((long)f3_2 * f5_2) + ((long)f4 * f4) + ((long)f9 * f9_38);
        long h9 = ((long)f0_2 * f9) + ((long)f1_2 * f8) + ((long)f2_2 * f7) + ((long)f3_2 * f6) + ((long)f4_2 * f5);

        ReduceCarry(h0, h1, h2, h3, h4, h5, h6, h7, h8, h9, out result);
    }

    // ── Mul121666 ─────────────────────────────────────────────────────────────

    /// <summary>h = this * 121666  (Montgomery ladder constant)</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public readonly void Mul121666(out FieldElement result)
    {
        long h0 = (long)E0 * 121666, h1 = (long)E1 * 121666;
        long h2 = (long)E2 * 121666, h3 = (long)E3 * 121666;
        long h4 = (long)E4 * 121666, h5 = (long)E5 * 121666;
        long h6 = (long)E6 * 121666, h7 = (long)E7 * 121666;
        long h8 = (long)E8 * 121666, h9 = (long)E9 * 121666;

        // Odd-only carry first pass.
        long c9 = (h9 + (1 << 24)) >> 25; h0 += c9 * 19; h9 -= c9 << 25;
        long c1 = (h1 + (1 << 24)) >> 25; h2 += c1; h1 -= c1 << 25;
        long c3 = (h3 + (1 << 24)) >> 25; h4 += c3; h3 -= c3 << 25;
        long c5 = (h5 + (1 << 24)) >> 25; h6 += c5; h5 -= c5 << 25;
        long c7 = (h7 + (1 << 24)) >> 25; h8 += c7; h7 -= c7 << 25;
        // Even carry second pass.
        long c0 = (h0 + (1 << 25)) >> 26; h1 += c0; h0 -= c0 << 26;
        long c2 = (h2 + (1 << 25)) >> 26; h3 += c2; h2 -= c2 << 26;
        long c4 = (h4 + (1 << 25)) >> 26; h5 += c4; h4 -= c4 << 26;
        long c6 = (h6 + (1 << 25)) >> 26; h7 += c6; h6 -= c6 << 26;
        long c8 = (h8 + (1 << 25)) >> 26; h9 += c8; h8 -= c8 << 26;

        result.E0 = (int)h0;
        result.E1 = (int)h1;
        result.E2 = (int)h2;
        result.E3 = (int)h3;
        result.E4 = (int)h4;
        result.E5 = (int)h5;
        result.E6 = (int)h6;
        result.E7 = (int)h7;
        result.E8 = (int)h8;
        result.E9 = (int)h9;
    }

    // ── Invert ───────────────────────────────────────────────────────────────

    /// <summary>h = this^(-1) mod p  (via Fermat: p-2 exponentiation)</summary>
    /// <remarks>
    /// Optimized with 'out' parameters to ensure zero-copy of the 40-byte FieldElement struct
    /// across the extensive exponentiation chain.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public readonly void Invert(out FieldElement result)
    {
        // t0 = this²
        this.Square(out FieldElement t0);

        // t1 = (t0²)² = t0⁴
        t0.Square(out FieldElement t1);
        t1.Square(out t1);

        // t1 = this * t1 = this⁵
        this.Multiply(in t1, out t1);

        // t0 = t0 * t1
        t0.Multiply(in t1, out t0);

        // t2 = t0²
        t0.Square(out FieldElement t2);

        // t1 = t1 * t2
        t1.Multiply(in t2, out t1);

        // 5 squares unrolled: t2 = t1^(2^5)
        t1.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);

        // t1 = t2 * t1
        t2.Multiply(in t1, out t1);

        // 10 squares unrolled: t2 = t1^(2^10)
        t1.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);

        // t1 = t2 * t1
        t2.Multiply(in t1, out t1);

        // 20 squares: t3 = t1^(2^20)
        t1.Square(out FieldElement t3);
        for (int i = 1; i < 20; i++)
        {
            t3.Square(out t3);
        }

        // t2 = t3 * t2
        t3.Multiply(in t2, out t2);

        // 10 squares unrolled
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);
        t2.Square(out t2);

        // t1 = t2 * t1
        t2.Multiply(in t1, out t1);

        // 50 squares
        t1.Square(out t2);
        for (int i = 1; i < 50; i++)
        {
            t2.Square(out t2);
        }

        // t2 = t2 * t1
        t2.Multiply(in t1, out t2);

        // 100 squares
        t2.Square(out t3);
        for (int i = 1; i < 100; i++)
        {
            t3.Square(out t3);
        }

        // t2 = t3 * t2
        t3.Multiply(in t2, out t2);

        // 50 squares
        t2.Square(out t2);
        for (int i = 1; i < 50; i++)
        {
            t2.Square(out t2);
        }

        // t1 = t2 * t1
        t2.Multiply(in t1, out t1);

        // 5 squares unrolled
        t1.Square(out t1);
        t1.Square(out t1);
        t1.Square(out t1);
        t1.Square(out t1);
        t1.Square(out t1);

        // result = t1 * t0
        t1.Multiply(in t0, out result);
    }

    // ── Constant-time helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Replaces (f, g) with (g, f) if b == 1; leaves them unchanged if b == 0.
    /// Runs in constant time (no data-dependent branches).
    /// </summary>
    /// <param name="f"></param>
    /// <param name="g"></param>
    /// <param name="b"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void CSwap(ref FieldElement f, ref FieldElement g, int b)
    {
        int t;
        int mask = -b; // 0x00000000 or 0xFFFFFFFF

        t = mask & (f.E0 ^ g.E0); f.E0 ^= t; g.E0 ^= t;
        t = mask & (f.E1 ^ g.E1); f.E1 ^= t; g.E1 ^= t;
        t = mask & (f.E2 ^ g.E2); f.E2 ^= t; g.E2 ^= t;
        t = mask & (f.E3 ^ g.E3); f.E3 ^= t; g.E3 ^= t;
        t = mask & (f.E4 ^ g.E4); f.E4 ^= t; g.E4 ^= t;
        t = mask & (f.E5 ^ g.E5); f.E5 ^= t; g.E5 ^= t;
        t = mask & (f.E6 ^ g.E6); f.E6 ^= t; g.E6 ^= t;
        t = mask & (f.E7 ^ g.E7); f.E7 ^= t; g.E7 ^= t;
        t = mask & (f.E8 ^ g.E8); f.E8 ^= t; g.E8 ^= t;
        t = mask & (f.E9 ^ g.E9); f.E9 ^= t; g.E9 ^= t;
    }

    /// <summary>Copies <paramref name="src"/> into <paramref name="dst"/>.</summary>
    /// <param name="dst"></param>
    /// <param name="src"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Copy(ref FieldElement dst, in FieldElement src)
    {
        dst.E0 = src.E0; dst.E1 = src.E1; dst.E2 = src.E2;
        dst.E3 = src.E3; dst.E4 = src.E4; dst.E5 = src.E5;
        dst.E6 = src.E6; dst.E7 = src.E7; dst.E8 = src.E8;
        dst.E9 = src.E9;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Shared carry-reduction path used by <see cref="Multiply"/> and <see cref="Square"/>.
    /// Returns a new <see cref="FieldElement"/> with all limbs reduced.
    /// All temporaries are scalar locals — zero heap allocation.
    /// </summary>
    /// <param name="h0"></param>
    /// <param name="h1"></param>
    /// <param name="h2"></param>
    /// <param name="h3"></param>
    /// <param name="h4"></param>
    /// <param name="h5"></param>
    /// <param name="h6"></param>
    /// <param name="h7"></param>
    /// <param name="h8"></param>
    /// <param name="h9"></param>
    /// <param name="result"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void ReduceCarry(
        long h0, long h1, long h2, long h3, long h4,
        long h5, long h6, long h7, long h8, long h9, out FieldElement result)
    {
        long c0, c1, c2, c3, c4, c5, c6, c7, c8, c9;

        c0 = (h0 + (1 << 25)) >> 26; h1 += c0; h0 -= c0 << 26;
        c4 = (h4 + (1 << 25)) >> 26; h5 += c4; h4 -= c4 << 26;
        c1 = (h1 + (1 << 24)) >> 25; h2 += c1; h1 -= c1 << 25;
        c5 = (h5 + (1 << 24)) >> 25; h6 += c5; h5 -= c5 << 25;
        c2 = (h2 + (1 << 25)) >> 26; h3 += c2; h2 -= c2 << 26;
        c6 = (h6 + (1 << 25)) >> 26; h7 += c6; h6 -= c6 << 26;
        c3 = (h3 + (1 << 24)) >> 25; h4 += c3; h3 -= c3 << 25;
        c7 = (h7 + (1 << 24)) >> 25; h8 += c7; h7 -= c7 << 25;
        c4 = (h4 + (1 << 25)) >> 26; h5 += c4; h4 -= c4 << 26;
        c8 = (h8 + (1 << 25)) >> 26; h9 += c8; h8 -= c8 << 26;
        c9 = (h9 + (1 << 24)) >> 25; h0 += c9 * 19; h9 -= c9 << 25;
        c0 = (h0 + (1 << 25)) >> 26; h1 += c0; h0 -= c0 << 26;

        result.E0 = (int)h0;
        result.E1 = (int)h1;
        result.E2 = (int)h2;
        result.E3 = (int)h3;
        result.E4 = (int)h4;
        result.E5 = (int)h5;
        result.E6 = (int)h6;
        result.E7 = (int)h7;
        result.E8 = (int)h8;
        result.E9 = (int)h9;
    }

    /// <summary>Reads 3 bytes little-endian from <paramref name="src"/> at <paramref name="offset"/>.</summary>
    /// <param name="src"></param>
    /// <param name="offset"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long Load3(System.ReadOnlySpan<byte> src, int offset) => src[offset] | ((long)src[offset + 1] << 8) | ((long)src[offset + 2] << 16);

    /// <summary>Reads 4 bytes little-endian from <paramref name="src"/> at <paramref name="offset"/>.</summary>
    /// <param name="src"></param>
    /// <param name="offset"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long Load4(System.ReadOnlySpan<byte> src, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(offset, 4));
}
