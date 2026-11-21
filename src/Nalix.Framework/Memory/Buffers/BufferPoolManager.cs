// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Injection;
using Nalix.Environment.Configuration;
using Nalix.Framework.Memory.Internal.Buffers;
using Nalix.Framework.Options;

namespace Nalix.Framework.Memory.Buffers;

/// <summary>
/// Manages pooled byte buffers by wrapping the shared ArrayPool and tracking diagnostics.
/// </summary>
[DebuggerNonUserCode]
[Injectable(typeof(IBufferPoolManager))]
public sealed partial class BufferPoolManager : IBufferPoolManager, IDisposable
{
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
    private readonly BufferOptions _config;

    // Diagnostics counters, one padded slot per processor (summed by the report): a single shared counter
    // made every core write the same cache line on every rent and return.
    private readonly CounterSlot[] _counters = new CounterSlot[CounterSlotCount() + 1];
    private readonly DateTime _startTime = DateTime.UtcNow;

    // Leak detection
    private ConcurrentBag<WeakReference<BufferSentinel>> _sentinelTracker = new();
    private readonly ConditionalWeakTable<byte[], BufferSentinel> _activeSentinels = new();
    private readonly Lock _sentinelTrackerLock = new();
    private int _trackedSentinels;
    private int _sentinelPruneAt = SENTINEL_PRUNE_MIN;
    private int _disposed;

    /// <summary>Minimum number of tracked sentinels before the tracker is pruned of returned buffers.</summary>
    private const int SENTINEL_PRUNE_MIN = 512;

    // Double-return guard: the identity hashes of buffers currently held by the shared pool, in a lock-free
    // table (0 = empty). It used to hold weak references under one lock, which every rent and return of every
    // thread went through. Two buffers sharing a hash only make a return look doubled, which drops that buffer
    // instead of pooling it: harmless.
    private const int FREE_TRACK_SIZE = 4096; // must be a power of two
    private const int FREE_TRACK_PROBES = 8;
    private readonly int[] _freeTrack = new int[FREE_TRACK_SIZE];

    /// <summary>Gets the largest buffer size. Returns 0 as sizing is dynamically managed by ArrayPool.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Architectural interface alignment")]
    public int MaxBufferSize => 0;

    /// <summary>Gets the smallest buffer size. Returns 0 as sizing is dynamically managed by ArrayPool.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Architectural interface alignment")]
    public int MinBufferSize => 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferPoolManager"/> class.
    /// </summary>
    public BufferPoolManager() : this(bufferConfig: null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferPoolManager"/> class.
    /// </summary>
    /// <param name="bufferConfig">
    /// The buffer configuration to use. If <see langword="null"/>, the default configuration is loaded.
    /// </param>
    public BufferPoolManager(BufferOptions? bufferConfig = null)
    {
        BufferOptions config = bufferConfig ?? ConfigurationManager.Instance.Get<BufferOptions>();
        config.Validate();
        _config = config;
    }

    /// <summary>Rents a buffer of at least the requested size.</summary>
    /// <param name="minimumLength">The minimum number of bytes required.</param>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Rent(int minimumLength = 256)
    {
        byte[] array = _pool.Rent(minimumLength);
        this.UntrackFree(array);

        ref CounterSlot counters = ref this.Counters;
        _ = Interlocked.Increment(ref counters.Rents);
        _ = Interlocked.Add(ref counters.BytesRented, array.Length);

        if (_config.EnableBufferLeakDetection)
        {
            BufferSentinel sentinel = new(array, _config.EnableBufferLeakStackTrace);
            _activeSentinels.Add(array, sentinel);
            this.TrackSentinel(sentinel);
        }

        return array;
    }

    /// <summary>
    /// Adds a sentinel to the leak tracker, pruning returned or collected entries once the tracker grows past its threshold.
    /// </summary>
    private void TrackSentinel(BufferSentinel sentinel)
    {
        lock (_sentinelTrackerLock)
        {
            if (_trackedSentinels >= _sentinelPruneAt)
            {
                this.PruneSentinelTrackerLocked();
            }

            _sentinelTracker.Add(new WeakReference<BufferSentinel>(sentinel));
            _trackedSentinels++;
        }
    }

    /// <summary>
    /// Records a buffer as pooled. Returns <c>false</c> if it is already pooled (a double return).
    /// Buffers that find no free slot are not tracked and are pooled as before.
    /// </summary>
    /// <remarks>
    /// The whole probe window is searched before a slot is taken, so a double return is caught wherever the
    /// first return landed. Two threads returning the same buffer at once both take a slot and then look again:
    /// one that sees the other's entry gives its slot back and reports a double return. With full fences
    /// between taking and looking, at least one sees the other, so the buffer is never pooled twice (at worst
    /// neither pools it).
    /// </remarks>
    private bool TrackFree(byte[] array)
    {
        int key = FreeTrackKey(array);
        int start = key & (FREE_TRACK_SIZE - 1);

        while (true)
        {
            int empty = -1;
            for (int probe = 0; probe < FREE_TRACK_PROBES; probe++)
            {
                int index = (start + probe) & (FREE_TRACK_SIZE - 1);
                int value = Volatile.Read(ref _freeTrack[index]);
                if (value == key)
                {
                    return false;
                }

                if (value == 0 && empty < 0)
                {
                    empty = index;
                }
            }

            if (empty < 0)
            {
                return true;
            }

            if (Interlocked.CompareExchange(ref _freeTrack[empty], key, 0) != 0)
            {
                continue; // The slot was just taken: search again.
            }

            for (int probe = 0; probe < FREE_TRACK_PROBES; probe++)
            {
                int index = (start + probe) & (FREE_TRACK_SIZE - 1);
                if (index != empty && Volatile.Read(ref _freeTrack[index]) == key)
                {
                    Volatile.Write(ref _freeTrack[empty], 0);
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Removes a buffer from the pooled set when it is handed out.</summary>
    private void UntrackFree(byte[] array)
    {
        int key = FreeTrackKey(array);
        int start = key & (FREE_TRACK_SIZE - 1);

        for (int probe = 0; probe < FREE_TRACK_PROBES; probe++)
        {
            int index = (start + probe) & (FREE_TRACK_SIZE - 1);
            if (Volatile.Read(ref _freeTrack[index]) == key &&
                Interlocked.CompareExchange(ref _freeTrack[index], 0, key) == key)
            {
                return;
            }
        }
    }

    /// <summary>The buffer's identity hash, never 0 (the empty slot).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FreeTrackKey(byte[] array)
    {
        int hash = RuntimeHelpers.GetHashCode(array);
        return hash == 0 ? 1 : hash;
    }

    /// <summary>Gets the counter slot of the current processor.</summary>
    // Slot 0 is never used: it shares a cache line with the array's length, which every core reads for the bounds
    // check, so a core writing it slowed down every other core's access to the array.
    private ref CounterSlot Counters
        => ref _counters[(Thread.GetCurrentProcessorId() & (_counters.Length - 2)) + 1];

    /// <summary>Sums the counter slots.</summary>
    private (long Rents, long Returns, long BytesRented) ReadCounters()
    {
        long rents = 0, returns = 0, bytes = 0;
        for (int i = 0; i < _counters.Length; i++)
        {
            ref CounterSlot slot = ref _counters[i];
            rents += Volatile.Read(ref slot.Rents);
            returns += Volatile.Read(ref slot.Returns);
            bytes += Volatile.Read(ref slot.BytesRented);
        }

        return (rents, returns, bytes);
    }

    /// <summary>A power of two of at least the processor count, capped at 64.</summary>
    private static int CounterSlotCount()
        => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)System.Math.Clamp(System.Environment.ProcessorCount, 1, 64));

    /// <summary>
    /// The counters of one processor. 128 bytes apart from the next slot's: an array's elements start 16 bytes into the
    /// object, which is not cache-line aligned, so 64-byte slots straddled two lines and each line was shared by two
    /// processors' slots.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 128)]
    private struct CounterSlot
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public long Rents;

        [System.Runtime.InteropServices.FieldOffset(8)]
        public long Returns;

        [System.Runtime.InteropServices.FieldOffset(16)]
        public long BytesRented;
    }

    private void PruneSentinelTrackerLocked()
    {
        ConcurrentBag<WeakReference<BufferSentinel>> kept = new();
        int survivors = 0;

        foreach (WeakReference<BufferSentinel> weakRef in _sentinelTracker)
        {
            if (weakRef.TryGetTarget(out BufferSentinel? sentinel) && !sentinel.IsReturned)
            {
                kept.Add(weakRef);
                survivors++;
            }
        }

        _sentinelTracker = kept;
        _trackedSentinels = survivors;
        _sentinelPruneAt = System.Math.Max(SENTINEL_PRUNE_MIN, survivors * 2);
    }

    /// <summary>Returns a buffer to the appropriate pool.</summary>
    /// <param name="array">The buffer to return.</param>
    /// <param name="arrayClear">Whether the buffer should be cleared before returning it.</param>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(byte[]? array, bool arrayClear = false)
    {
        if (array is null)
        {
            return;
        }

        // Reject a double return before anything else: the buffer may already belong to another renter.
        if (!this.TrackFree(array))
        {
            return;
        }

        if (arrayClear)
        {
            array.AsSpan().Clear();
        }

        if (_config.EnableBufferLeakDetection)
        {
            if (_activeSentinels.TryGetValue(array, out BufferSentinel? sentinel))
            {
                sentinel.MarkReturned();
                _ = _activeSentinels.Remove(array);
            }
        }

        _pool.Return(array, clearArray: false);
        _ = Interlocked.Increment(ref this.Counters.Returns);
    }

    /// <summary>
    /// Releases all resources of the buffer pools.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _sentinelTracker = new();
        GC.SuppressFinalize(this);
    }
}
