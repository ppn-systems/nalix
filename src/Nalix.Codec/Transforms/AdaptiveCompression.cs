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
/// After <see cref="MissesBeforeBackoff"/> attempts in a row that did not pay off, the opcode backs off: a thread
/// then attempts only one frame in <see cref="MinSkipFrames"/> of that opcode. Each further failed probe doubles
/// that interval up to <see cref="MaxSkipFrames"/>; one probe that pays off returns the opcode to always
/// compressing. A skipped frame goes out uncompressed, exactly as an attempt that did not pay off would, so
/// receivers see no difference.
/// </para>
/// <para>
/// Every opcode has its own entry (one byte, 64 KB in all), so opcodes never share state. Deciding whether to
/// attempt only reads that entry: the probe cadence comes from a per-thread counter, so frames sent from several
/// cores do not write the same cache line. The entry is written only when an attempt finishes, and those writes are
/// not synchronized on purpose: a lost update only shifts when the next probe happens.
/// </para>
/// </remarks>
internal static class AdaptiveCompression
{
    internal const int MissesBeforeBackoff = 4;
    internal const int MinSkipFrames = 32;
    internal const int MaxSkipFrames = 1024;

    // Entry layout: bits 0-2 = backoff level (0 = always attempt; level L attempts 1 frame in MinSkipFrames << (L - 1)),
    // bits 3-4 = attempts in a row that did not pay off while at level 0.
    private const int LevelMask = 0b111;
    private const int MissShift = 3;
    private const int MaxLevel = 6; // MinSkipFrames << (MaxLevel - 1) == MaxSkipFrames

    private static readonly byte[] s_state = new byte[ushort.MaxValue + 1];

    [ThreadStatic]
    private static uint s_frames;

    /// <summary>
    /// Returns <see langword="true"/> when the frame should try compression: always for an opcode that compresses,
    /// and one frame in the backoff interval (per thread) for an opcode that does not.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldAttempt(ushort opCode)
    {
        int level = s_state[opCode] & LevelMask;
        if (level == 0)
        {
            return true;
        }

        uint interval = (uint)MinSkipFrames << (level - 1);
        return (++s_frames & (interval - 1)) == 0;
    }

    /// <summary>Records whether an attempt for the opcode paid off, and backs off after repeated misses.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Record(ushort opCode, bool worthwhile)
    {
        if (worthwhile)
        {
            if (s_state[opCode] != 0)
            {
                s_state[opCode] = 0;
            }

            return;
        }

        RECORD_MISS(opCode);
    }

    /// <summary>Returns the current backoff interval of the opcode in frames; 1 when it is always attempted (tests).</summary>
    internal static int Interval(ushort opCode)
    {
        int level = s_state[opCode] & LevelMask;
        return level == 0 ? 1 : MinSkipFrames << (level - 1);
    }

    /// <summary>Forgets what was learned about every opcode, and restarts this thread's probe counter (tests).</summary>
    internal static void Reset()
    {
        Array.Clear(s_state);
        s_frames = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RECORD_MISS(ushort opCode)
    {
        int state = s_state[opCode];
        int level = state & LevelMask;

        if (level != 0)
        {
            // A failed probe: back off further.
            s_state[opCode] = (byte)Math.Min(level + 1, MaxLevel);
            return;
        }

        int misses = (state >> MissShift) + 1;
        s_state[opCode] = misses >= MissesBeforeBackoff ? (byte)1 : (byte)(misses << MissShift);
    }
}
