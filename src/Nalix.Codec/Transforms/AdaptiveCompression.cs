// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;

namespace Nalix.Codec.Transforms;

/// <summary>
/// Learns, per opcode, whether outbound payloads compress, so <see cref="FramePipeline"/> can skip LZ4 for
/// opcodes whose payloads keep failing to compress (already compressed, encrypted or random data). For an
/// incompressible 1 KB payload the attempt costs as much CPU as the rest of the send path.
/// </summary>
/// <remarks>
/// <para>
/// After <see cref="MissesBeforeBackoff"/> attempts in a row that did not pay off, the next frames of that opcode
/// skip the attempt. The window starts at <see cref="MinSkipFrames"/> and doubles on every further failed probe up
/// to <see cref="MaxSkipFrames"/>; one probe that pays off returns the opcode to always compressing. A skipped frame
/// goes out uncompressed, exactly as an attempt that did not pay off would, so receivers see no difference.
/// </para>
/// <para>
/// Opcodes are hashed into <see cref="Buckets"/> buckets. The state is process-wide and its updates are not
/// synchronized on purpose: a lost update only shifts when the next probe happens.
/// </para>
/// </remarks>
internal static class AdaptiveCompression
{
    internal const int Buckets = 4096;
    internal const int MissesBeforeBackoff = 4;
    internal const int MinSkipFrames = 32;
    internal const int MaxSkipFrames = 1024;

    private static readonly int[] s_skipRemaining = new int[Buckets];
    private static readonly int[] s_skipWindow = new int[Buckets];
    private static readonly byte[] s_misses = new byte[Buckets];

    /// <summary>Returns <see langword="false"/> while the opcode's skip window is open, counting the frame against it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldAttempt(ushort opCode)
    {
        int bucket = opCode & (Buckets - 1);
        int remaining = s_skipRemaining[bucket];
        if (remaining <= 0)
        {
            return true;
        }

        s_skipRemaining[bucket] = remaining - 1;
        return false;
    }

    /// <summary>Records whether an attempt for the opcode paid off, and opens a skip window after repeated misses.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Record(ushort opCode, bool worthwhile)
    {
        int bucket = opCode & (Buckets - 1);
        if (worthwhile)
        {
            if (s_misses[bucket] != 0 || s_skipWindow[bucket] != 0)
            {
                s_misses[bucket] = 0;
                s_skipWindow[bucket] = 0;
            }

            return;
        }

        RECORD_MISS(bucket);
    }

    /// <summary>Forgets what was learned about every opcode (tests).</summary>
    internal static void Reset()
    {
        Array.Clear(s_skipRemaining);
        Array.Clear(s_skipWindow);
        Array.Clear(s_misses);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RECORD_MISS(int bucket)
    {
        int misses = s_misses[bucket] + 1;
        int window = s_skipWindow[bucket];

        // Once a window has been opened, a single failed probe is enough to open the next, longer one.
        if (window == 0 && misses < MissesBeforeBackoff)
        {
            s_misses[bucket] = (byte)misses;
            return;
        }

        window = window == 0 ? MinSkipFrames : Math.Min(window * 2, MaxSkipFrames);
        s_misses[bucket] = 0;
        s_skipWindow[bucket] = window;
        s_skipRemaining[bucket] = window;
    }
}
