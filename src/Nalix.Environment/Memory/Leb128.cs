// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nalix.Environment.Internal;

namespace Nalix.Environment.Memory;

/// <summary>
/// Provides high-performance methods for encoding and decoding integers
/// using the Little-Endian Base 128 (LEB128) format.
/// Optimized with unsafe pointer arithmetic and branch-minimized paths.
/// </summary>
public static class Leb128
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>Maximum bytes a 32-bit LEB128 value can occupy.</summary>
    public const int MaxByteCount = 5;

    // ── Byte-count (branch-free via bit tricks) ───────────────────────────────

    /// <summary>
    /// Returns the number of bytes required to LEB128-encode <paramref name="value"/>.
    /// Uses a branch-free bit-scan approach: O(1), no comparisons.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetByteCount(uint value)
    {
        // Each 7-bit group needs one byte.
        // bit_length(0) = 1 → 1 byte; formula: (bits + 6) / 7
        // System.Numerics.BitOperations.Log2 is intrinsic on x64/ARM → single BSR/CLZ
        int bits = 32 - System.Numerics.BitOperations.LeadingZeroCount(value | 1);
        return (bits + 6) / 7;  // ceil(bits / 7)
    }

    /// <inheritdoc cref="GetByteCount(uint)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetByteCount(int value) => GetByteCount((uint)value);

    // ── Write (unsafe pointer, no bounds check) ───────────────────────────────

    /// <summary>
    /// Encodes <paramref name="value"/> into <paramref name="buffer"/> using LEB128.
    /// Uses unsafe pointer arithmetic to eliminate all bounds checks.
    /// </summary>
    /// <param name="buffer">Must have capacity ≥ <see cref="GetByteCount(uint)"/>.</param>
    /// <param name="value"></param>
    /// <returns>Number of bytes written (1–5).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int Write(Span<byte> buffer, uint value)
    {
        if (buffer.Length < GetByteCount(value))
        {
            Throw.Leb128BufferTooSmall();
        }

        fixed (byte* ptr = &MemoryMarshal.GetReference(buffer))
        {
            byte* p = ptr;

            // Unrolled: each iteration is a predictable pattern the JIT can pipeline
            while (value > 0x7Fu)
            {
                *p++ = (byte)(value | 0x80u);
                value >>= 7;
            }

            *p++ = (byte)value;
            return (int)(p - ptr);
        }
    }

    /// <inheritdoc cref="Write(Span{byte}, uint)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(Span<byte> buffer, int value) => Write(buffer, (uint)value);

    //// ── Write fully-unrolled (fastest for known small values) ─────────────────

    /// <summary>
    /// Fully-unrolled LEB128 write for values that fit in 32 bits.
    /// Eliminates the loop entirely — the JIT/CPU sees straight-line code.
    /// Prefer this on hot paths where the value range is known to be small.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int WriteUnrolled(Span<byte> buffer, uint value)
    {
        if (buffer.Length < GetByteCount(value))
        {
            Throw.Leb128BufferTooSmall();
        }

        fixed (byte* p = &MemoryMarshal.GetReference(buffer))
        {
            if (value < 0x80u) { p[0] = (byte)value; return 1; }
            if (value < 0x4000u) { p[0] = (byte)(value | 0x80u); p[1] = (byte)(value >> 7); return 2; }
            if (value < 0x20_0000u) { p[0] = (byte)(value | 0x80u); p[1] = (byte)((value >> 7) | 0x80u); p[2] = (byte)(value >> 14); return 3; }
            if (value < 0x1000_0000u) { p[0] = (byte)(value | 0x80u); p[1] = (byte)((value >> 7) | 0x80u); p[2] = (byte)((value >> 14) | 0x80u); p[3] = (byte)(value >> 21); return 4; }

            p[0] = (byte)(value | 0x80u);
            p[1] = (byte)((value >> 7) | 0x80u);
            p[2] = (byte)((value >> 14) | 0x80u);
            p[3] = (byte)((value >> 21) | 0x80u);
            p[4] = (byte)(value >> 28);
            return 5;
        }
    }

    // ── TryRead (unsafe, maximal inlining) ────────────────────────────────────

    /// <summary>
    /// Attempts to decode a LEB128-encoded <see cref="uint"/> from <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Source bytes.</param>
    /// <param name="value">Decoded value on success; 0 on failure.</param>
    /// <param name="bytesRead">Bytes consumed on success; 0 on failure.</param>
    /// <returns><see langword="true"/> if a complete sequence was found.</returns>
    /// <exception cref="FormatException">Sequence exceeds 5 bytes (overlong/corrupt).</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe bool TryRead(ReadOnlySpan<byte> buffer, out uint value, out int bytesRead)
    {
        fixed (byte* ptr = &MemoryMarshal.GetReference(buffer))
        {
            int len = buffer.Length;
            byte* p = ptr;
            byte* end = ptr + len;

            // Fast path: buffer has at least MaxByteCount bytes (most common case in framing)
            if (len >= MaxByteCount)
            {
                uint v = 0u;

                // Each line: read byte, check continuation bit, shift-accumulate
                byte b0 = p[0]; v = (uint)(b0 & 0x7F); if ((b0 & 0x80) == 0) { value = v; bytesRead = 1; return true; }
                byte b1 = p[1]; v |= (uint)(b1 & 0x7F) << 7; if ((b1 & 0x80) == 0) { value = v; bytesRead = 2; return true; }
                byte b2 = p[2]; v |= (uint)(b2 & 0x7F) << 14; if ((b2 & 0x80) == 0) { value = v; bytesRead = 3; return true; }
                byte b3 = p[3]; v |= (uint)(b3 & 0x7F) << 21; if ((b3 & 0x80) == 0) { value = v; bytesRead = 4; return true; }
                byte b4 = p[4]; v |= (uint)(b4 & 0x7F) << 28;

                if ((b4 & 0x80) != 0)
                {
                    Throw.OverlongLeb128();
                }

                value = v; bytesRead = 5; return true;
            }

            // Slow path: buffer is shorter than MaxByteCount (boundary reads)
            return TryReadSlow(p, end, out value, out bytesRead);
        }
    }

    /// <inheritdoc cref="TryRead(ReadOnlySpan{byte}, out uint, out int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryRead(ReadOnlySpan<byte> buffer, out int value, out int bytesRead)
    {
        bool ok = TryRead(buffer, out uint uval, out bytesRead);
        value = (int)uval;
        return ok;
    }

    // ── Slow path (out-of-line, keeps hot path icache-clean) ──────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]   // <-- deliberately NOT inlined
    private static unsafe bool TryReadSlow(byte* p, byte* end, out uint value, out int bytesRead)
    {
        uint v = 0u;
        int shift = 0;
        byte* cur = p;

        while (cur < end)
        {
            byte b = *cur++;
            v |= (uint)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                value = v;
                bytesRead = (int)(cur - p);
                return true;
            }

            shift += 7;

            if (shift == MaxByteCount * 7)
            {
                Throw.OverlongLeb128();
            }
        }

        // Buffer ended mid-sequence → incomplete
        value = 0;
        bytesRead = 0;
        return false;
    }
}
