// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nalix.Environment.Hashing;

/// <summary>
/// Provides a high-performance implementation of the xxHash32 hashing algorithm.
/// </summary>
/// <remarks>
/// <para>
/// This implementation is optimized for low allocation and high throughput scenarios.
/// It supports hardware-accelerated vector processing when available.
/// </para>
/// <para>
/// xxHash32 is a non-cryptographic hash algorithm designed for speed and
/// excellent distribution quality.
/// </para>
/// </remarks>
[SkipLocalsInit]
public static class XxHash32
{
    private const uint PRIME32_1 = 0x9E3779B1U;
    private const uint PRIME32_2 = 0x85EBCA77U;
    private const uint PRIME32_3 = 0xC2B2AE3DU;
    private const uint PRIME32_4 = 0x27D4EB2FU;
    private const uint PRIME32_5 = 0x165667B1U;

    /// <summary>
    /// Computes a 32-bit xxHash value for the specified data.
    /// </summary>
    /// <param name="data">
    /// The input data to hash.
    /// </param>
    /// <param name="seed">
    /// The optional hash seed value.
    /// </param>
    /// <returns>
    /// A 32-bit unsigned hash value.
    /// </returns>
    /// <remarks>
    /// This method uses vectorized processing when supported by the current runtime
    /// and hardware platform.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0)
    {
        int len = data.Length;
        if (len == 0)
        {
            return seed + PRIME32_5;
        }

        ref byte src = ref MemoryMarshal.GetReference(data);
        int i = 0;
        uint h;

        if (len >= 16)
        {
            uint v1 = seed + PRIME32_1 + PRIME32_2;
            uint v2 = seed + PRIME32_2;
            uint v3 = seed;
            uint v4 = seed - PRIME32_1;

            if (Vector128.IsHardwareAccelerated && len >= 32)
            {
                int limit = len - 15;

                while (i <= limit - 16)
                {
                    Vector128<uint> block = Vector128.LoadUnsafe(ref src, (uint)i).AsUInt32();

                    v1 = Round(v1, block.GetElement(0));
                    v2 = Round(v2, block.GetElement(1));
                    v3 = Round(v3, block.GetElement(2));
                    v4 = Round(v4, block.GetElement(3));

                    i += 16;
                }
            }
            else
            {
                int limit = len - 15;

                while (i < limit)
                {
                    v1 = Round(v1, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, i)));
                    v2 = Round(v2, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, i + 4)));
                    v3 = Round(v3, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, i + 8)));
                    v4 = Round(v4, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, i + 12)));

                    i += 16;
                }
            }

            h = BitOperations.RotateLeft(v1, 1) +
                BitOperations.RotateLeft(v2, 7) +
                BitOperations.RotateLeft(v3, 12) +
                BitOperations.RotateLeft(v4, 18);
        }
        else
        {
            h = seed + PRIME32_5;
        }

        h += (uint)len;

        // Process remaining 4-byte blocks.
        while (i + 3 < len)
        {
            h = Round(
                h,
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, i)),
                PRIME32_3,
                17,
                PRIME32_4);

            i += 4;
        }

        // Process remaining bytes.
        while (i < len)
        {
            h += Unsafe.Add(ref src, i) * PRIME32_5;
            h = BitOperations.RotateLeft(h, 11) * PRIME32_1;

            i++;
        }

        return Avalanche(h);
    }

    /// <summary>
    /// Computes a stable 32-bit hash value for a socket endpoint representation.
    /// </summary>
    /// <param name="hi">
    /// The high 64 bits of the address.
    /// </param>
    /// <param name="lo">
    /// The low 64 bits of the address.
    /// </param>
    /// <param name="isIPv6">
    /// <see langword="true"/> if the endpoint uses IPv6;
    /// otherwise, <see langword="false"/>.
    /// </param>
    /// <returns>
    /// A positive 32-bit hash value suitable for endpoint hashing scenarios.
    /// </returns>
    /// <remarks>
    /// This overload is intended for high-performance networking scenarios
    /// where allocating temporary endpoint objects should be avoided.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Compute(ulong hi, ulong lo, bool isIPv6)
    {
        Span<byte> buffer = stackalloc byte[17];

        Unsafe.WriteUnaligned(ref buffer[0], hi);
        Unsafe.WriteUnaligned(ref buffer[8], lo);

        buffer[16] = isIPv6 ? (byte)1 : (byte)0;

        return (int)(Compute(buffer) & 0x7FFFFFFF);
    }

    #region Private Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Round(
        uint acc,
        uint input,
        uint mul = PRIME32_2,
        int rot = 13,
        uint post = PRIME32_1)
    {
        acc += input * mul;
        acc = BitOperations.RotateLeft(acc, rot);
        acc *= post;

        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Avalanche(uint h)
    {
        h ^= h >> 15;
        h *= PRIME32_2;

        h ^= h >> 13;
        h *= PRIME32_3;

        h ^= h >> 16;

        return h;
    }

    #endregion Private Methods
}
