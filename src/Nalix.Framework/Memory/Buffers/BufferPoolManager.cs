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

    // Diagnostics counters
    private long _rentCount;
    private long _returnCount;
    private long _totalBytesRented;
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

    // Double-return guard: buffers currently held by the shared pool, held by weak reference.
    // Slots are reused, so tracking allocates only the first time a slot is filled.
    private const int FREE_TRACK_SIZE = 1024; // must be a power of two
    private const int FREE_TRACK_PROBES = 8;
    private readonly WeakReference<byte[]>?[] _freeTrack = new WeakReference<byte[]>?[FREE_TRACK_SIZE];
    private readonly Lock _freeTrackLock = new();

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
        UntrackFree(array);
        _ = Interlocked.Increment(ref _rentCount);
        _ = Interlocked.Add(ref _totalBytesRented, array.Length);

        if (_config.EnableBufferLeakDetection)
        {
            BufferSentinel sentinel = new(array, _config.EnableBufferLeakStackTrace);
            _activeSentinels.Add(array, sentinel);
            TrackSentinel(sentinel);
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
                PruneSentinelTrackerLocked();
            }

            _sentinelTracker.Add(new WeakReference<BufferSentinel>(sentinel));
            _trackedSentinels++;
        }
    }

    /// <summary>
    /// Records a buffer as pooled. Returns <c>false</c> if it is already pooled (a double return).
    /// Buffers that find no free slot are not tracked and are pooled as before.
    /// </summary>
    private bool TrackFree(byte[] array)
    {
        int start = RuntimeHelpers.GetHashCode(array);

        lock (_freeTrackLock)
        {
            int firstEmpty = -1;
            for (int probe = 0; probe < FREE_TRACK_PROBES; probe++)
            {
                int index = (start + probe) & (FREE_TRACK_SIZE - 1);
                WeakReference<byte[]>? slot = _freeTrack[index];

                if (slot is not null && slot.TryGetTarget(out byte[]? tracked))
                {
                    if (ReferenceEquals(tracked, array))
                    {
                        return false;
                    }
                }
                else if (firstEmpty < 0)
                {
                    firstEmpty = index;
                }
            }

            if (firstEmpty >= 0)
            {
                WeakReference<byte[]> slot = _freeTrack[firstEmpty] ??= new WeakReference<byte[]>(array);
                slot.SetTarget(array);
            }

            return true;
        }
    }

    /// <summary>Removes a buffer from the pooled set when it is handed out.</summary>
    private void UntrackFree(byte[] array)
    {
        int start = RuntimeHelpers.GetHashCode(array);

        lock (_freeTrackLock)
        {
            for (int probe = 0; probe < FREE_TRACK_PROBES; probe++)
            {
                WeakReference<byte[]>? slot = _freeTrack[(start + probe) & (FREE_TRACK_SIZE - 1)];

                if (slot is not null && slot.TryGetTarget(out byte[]? tracked) && ReferenceEquals(tracked, array))
                {
                    slot.SetTarget(null!);
                    return;
                }
            }
        }
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
        if (!TrackFree(array))
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
        _ = Interlocked.Increment(ref _returnCount);
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
