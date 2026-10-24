// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nalix.Codec.Internal.Memory;

/// <summary>
/// Low-level span helpers used by the encoders and decoders that need
/// branch-light length encoding and decoding.
/// </summary>
[DebuggerNonUserCode]
[SkipLocalsInit]
[EditorBrowsable(EditorBrowsableState.Never)]
internal static unsafe class SpanOps
{
    /// <summary>
    /// Writes a variable-length integer using 0xFF continuation bytes followed by a final remainder.
    /// </summary>
    /// <param name="dest">The destination buffer.</param>
    /// <param name="value">The value to encode.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is negative.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int WriteVarInt(byte* dest, int value)
    {
        // Negative values are invalid input and indicate a caller bug.
        if (value < 0)
        {
            THROW_OUT_OF_RANGE();
        }

        // Split the value into as many 0xFF markers as needed, then a final remainder byte.
        uint u = (uint)value;
        int ffCount = (int)(u / 255u);
        byte last = (byte)(u % 255u);

        byte* p = dest;

        // Write 0xFF blocks with wide stores to reduce loop overhead.
        const ulong F8 = 0xFFFFFFFFFFFFFFFFul;

        // 16-byte chunks (2x ulong).
        while (ffCount >= 16)
        {
            // write 16 bytes of 0xFF
            *(ulong*)p = F8; *(ulong*)(p + 8) = F8;
            p += 16;
            ffCount -= 16;
        }

        // 8-byte chunk.
        if (ffCount >= 8)
        {
            *(ulong*)p = F8;
            p += 8;
            ffCount -= 8;
        }

        // 4-byte chunk.
        if (ffCount >= 4)
        {
            // 0xFFFFFFFF as uint
            *(uint*)p = 0xFFFFFFFFu;
            p += 4;
            ffCount -= 4;
        }

        // Tail: 0..3 single bytes.
        switch (ffCount)
        {
            case 3: p[0] = 255; p[1] = 255; p[2] = 255; p += 3; break;
            case 2: p[0] = 255; p[1] = 255; p += 2; break;
            case 1: p[0] = 255; p += 1; break; // 0
            default:
                break;
        }

        // Final remainder (<255) terminates the run of continuation bytes.
        *p = last;
        p += 1;

        return (int)(p - dest);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_OUT_OF_RANGE()
        => throw new ArgumentOutOfRangeException("value", "WriteVarInt does not support negative values.");

    /// <summary>
    /// Reads a variable-length integer that was encoded with 0xFF continuation bytes.
    /// </summary>
    /// <param name="src">A reference to the current source pointer.</param>
    /// <param name="srcEnd">The end of the readable source range.</param>
    /// <param name="value">When the method returns, contains the decoded value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int ReadVarInt(ref byte* src, byte* srcEnd, out int value)
    {
        value = 0;
        int bytesRead = 0;

        // Fast path: look ahead in 8-byte chunks while we know enough input remains.
        while ((ulong)(srcEnd - src) >= 8)
        {
            if (src[0] != 255)
            {
                break;
            }

            // k = 1
            if (src[1] != 255)
            {
                const int add = 255 * 1;
                if (value > int.MaxValue - add) { value = -1; return bytesRead; }
                value += add; src += 1; bytesRead += 1; goto Terminate;
            }
            // k = 2
            if (src[2] != 255)
            {
                const int add = 255 * 2;
                if (value > int.MaxValue - add) { value = -1; return bytesRead; }
                value += add; src += 2; bytesRead += 2; goto Terminate;
            }
            // k = 3
            if (src[3] != 255)
            {
                const int add = 255 * 3;
                if (value > int.MaxValue - add) { value = -1; return bytesRead; }
                value += add; src += 3; bytesRead += 3; goto Terminate;
            }
            // k = 4
            if (src[4] != 255)
            {
                const int add = 255 * 4;
                if (value > int.MaxValue - add) { value = -1; return bytesRead; }
                value += add; src += 4; bytesRead += 4; goto Terminate;
            }
            // k = 5
            if (src[5] != 255)
            {
                const int add = 255 * 5;
                if (value > int.MaxValue - add) { value = -1; return bytesRead; }
                value += add; src += 5; bytesRead += 5; goto Terminate;
            }
            // k = 6
            if (src[6] != 255)
            {
                const int add = 255 * 6;
                if (value > int.MaxValue - add) { value = -1; return bytesRead; }
                value += add; src += 6; bytesRead += 6; goto Terminate;
            }
            // k = 7
            if (src[7] != 255)
            {
                const int add = 255 * 7;
                if (value > int.MaxValue - add) { value = -1; return bytesRead; }
                value += add; src += 7; bytesRead += 7; goto Terminate;
            }

            // Eight continuation bytes in a row.
            const int add8 = 255 * 8;
            if (value > int.MaxValue - add8) { value = -1; return bytesRead; }
            value += add8; src += 8; bytesRead += 8;
            continue;

        Terminate:
            break;
        }

        // Scalar tail for the final few continuation bytes.
        while (src < srcEnd && *src == 255)
        {
            if (value > int.MaxValue - 255) { value = -1; return bytesRead; }
            value += 255; src++; bytesRead++;
        }

        // A terminating byte < 255 must be present.
        if (src >= srcEnd) { value = -1; return bytesRead; }

        int last = *src; // 0..254
        if (value > int.MaxValue - last) { value = -1; return bytesRead; }
        value += last; src++; bytesRead++;

        return bytesRead;
    }
}
