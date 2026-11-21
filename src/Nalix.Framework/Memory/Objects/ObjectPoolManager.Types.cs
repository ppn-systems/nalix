// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Framework.Memory.Objects;

public sealed partial class ObjectPoolManager
{
    #region Nested Types

    /// <summary>
    /// Detailed metrics for tracking pool performance and health.
    /// </summary>
    private sealed class PoolMetrics
    {
        /// <summary>
        /// Gets, Returns, hits and misses of this type, one padded slot per processor (the type is shared by every
        /// thread renting it, so a single counter made them all write one cache line). Summed when read.
        /// </summary>
        private readonly StatSlot[] _stats = new StatSlot[StatSlotCount()];

        public long TotalGets => SumOf(_stats, StatField.Gets);

        public long TotalReturns => SumOf(_stats, StatField.Returns);

        /// <summary>
        /// Failed to get from pool, created new
        /// </summary>
        public long CacheMisses => SumOf(_stats, StatField.Misses);

        /// <summary>
        /// Got from pool successfully
        /// </summary>
        public long CacheHits => SumOf(_stats, StatField.Hits);

        /// <summary>Gets the counter slot of the current processor.</summary>
        public ref StatSlot Current => ref _stats[System.Threading.Thread.GetCurrentProcessorId() & (_stats.Length - 1)];

        public void Reset(StatField field) => ResetOf(_stats, field);

        /// <summary>
        /// Number of objects evicted/trimmed from this pool.
        /// </summary>
        public long TrimCount;

        public long TotalCreated;
        public long TotalDisposed;
        public long TotalDropped;
        /// <summary>The recent peak of outstanding objects the trim floor protects (see UPDATE_TRIM_PEAK).</summary>
        public long TrimPeak;

        /// <summary>The lifetime peak seen by the previous trim cycle.</summary>
        public long LastTrimLifetimePeak;

        /// <summary>
        /// <see cref="System.Environment.TickCount64"/> at the last Get or Return, 0 before the first. Written only
        /// when it changes (at most once a millisecond): every thread using the type would otherwise write this
        /// cache line on every operation, and <see cref="DateTime.UtcNow"/> costs far more than the pooling itself.
        /// </summary>
        public long LastAccessTick;
        public string? LastAccessType;

        /// <summary>Gets the time of the last Get or Return, or <see cref="DateTime.MinValue"/> before the first.</summary>
        public DateTime LastAccessUtc
        {
            get
            {
                long tick = System.Threading.Volatile.Read(ref this.LastAccessTick);
                return tick == 0 ? DateTime.MinValue : DateTime.UtcNow.AddMilliseconds(tick - System.Environment.TickCount64);
            }
        }

        /// <summary>Records an access; a no-op when nothing changed since the last one.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Touch(string type)
        {
            long now = System.Environment.TickCount64;
            if (this.LastAccessTick != now)
            {
                this.LastAccessTick = now;
            }

            if (!ReferenceEquals(this.LastAccessType, type))
            {
                this.LastAccessType = type;
            }
        }
        public int ConsecutiveFailures;

        /// <summary>
        /// Number of objects currently checked out (Get without Return)
        /// </summary>
        public long Outstanding;

        /// <summary>
        /// Maximum concurrent outstanding objects recorded.
        /// </summary>
        public long PeakOutstanding;

        // Diagnostic Metrics (Only populated when diagnostics enabled)
        public long TotalLifetimeTicks;
        public long MaxLifetimeTicks;
        public long[]? LifetimeReservoir;
        public int ReservoirIndex;

        public long LastHealthGets;
        public long LastHealthHits;
        public long LastHealthMisses;
        public long LastHealthPeakOutstanding;
        public long LastPoolFailureLogUtcTicks;
        public int LastPoolFailureSeverity;

        public long LastTrimGets;
        public long LastTrimHits;
    }

    #endregion Nested Types


    /// <summary>The counters of one processor, alone on a 64-byte cache line.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 64)]
    private struct StatSlot
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public long Gets;

        [System.Runtime.InteropServices.FieldOffset(8)]
        public long Returns;

        [System.Runtime.InteropServices.FieldOffset(16)]
        public long Hits;

        [System.Runtime.InteropServices.FieldOffset(24)]
        public long Misses;
    }

    private enum StatField
    {
        Gets,
        Returns,
        Hits,
        Misses,
    }

    /// <summary>Gets the counter slot of the current processor.</summary>
    private ref StatSlot CurrentStats
        => ref _stats[System.Threading.Thread.GetCurrentProcessorId() & (_stats.Length - 1)];

    /// <summary>A power of two of at least the processor count, capped at 64.</summary>
    private static int StatSlotCount()
        => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(System.Environment.ProcessorCount, 1, 64));

    private static ref long StatOf(ref StatSlot slot, StatField field)
    {
        switch (field)
        {
            case StatField.Gets:
                return ref slot.Gets;
            case StatField.Returns:
                return ref slot.Returns;
            case StatField.Hits:
                return ref slot.Hits;
            case StatField.Misses:
            default:
                return ref slot.Misses;
        }
    }

    private long SumStats(StatField field) => SumOf(_stats, field);

    private void ResetStats(StatField field) => ResetOf(_stats, field);

    private static long SumOf(StatSlot[] stats, StatField field)
    {
        long sum = 0;
        for (int i = 0; i < stats.Length; i++)
        {
            sum += System.Threading.Volatile.Read(ref StatOf(ref stats[i], field));
        }

        return sum;
    }

    private static void ResetOf(StatSlot[] stats, StatField field)
    {
        for (int i = 0; i < stats.Length; i++)
        {
            _ = System.Threading.Interlocked.Exchange(ref StatOf(ref stats[i], field), 0);
        }
    }
}
