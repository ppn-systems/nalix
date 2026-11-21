// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;

namespace Nalix.Framework.Memory.Internal.PoolTypes;

/// <summary>
/// Type-specific object pool implementation backed by a preallocated fixed-slot array.
/// <para>
/// Objects are stored in <see cref="IPoolable"/>?[] slots.
/// <see cref="TryPop"/> scans slots and uses <see cref="Interlocked.Exchange{T}(ref T, T)"/>
/// to atomically claim a non-null slot.
/// <see cref="TryPush"/> scans slots and uses <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>
/// to atomically fill an empty slot.
/// This design allocates only once during construction and keeps hot-path operations allocation-free.
/// </para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="TypePool"/> class.
/// </remarks>
/// <param name="maxCapacity">The maximum capacity of this pool.</param>
[SkipLocalsInit]
[EditorBrowsable(EditorBrowsableState.Never)]
internal class TypePool(int maxCapacity)
{
    #region Fields

    private int _maxCapacity = maxCapacity;

    /// <summary>
    /// Fixed-slot array. Individual slots are atomically swapped between null and non-null.
    /// The reference only changes when <see cref="SetMaxCapacity"/> grows the pool.
    /// </summary>
    private IPoolable?[] _slots = new IPoolable?[maxCapacity > 0 ? maxCapacity : 1];

    /// <summary>Serializes slot-array growth; hot-path readers never take this lock.</summary>
    private readonly Lock _growLock = new();

    /// <summary>
    /// Count of objects in the pool, one padded slot per processor: a push adds one to the slot of the pushing
    /// core, a pop takes one from the popping core's, so a single slot can go negative but the sum is the count.
    /// One shared counter made every core write the same cache line on every push and pop, and cost most of a
    /// pooled Get and Return on a multi-core server. <see cref="AvailableCount"/> stays a best-effort figure.
    /// </summary>
    private readonly CountSlot[] _counts = new CountSlot[CountSlotCount()];

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets the number of objects available in this pool.
    /// </summary>
    public int AvailableCount => System.Math.Max(0, this.SumCounts());

    /// <summary>
    /// Gets the maximum capacity of this pool.
    /// </summary>
    public int MaxCapacity => Volatile.Read(ref _maxCapacity);

    #endregion Properties

    #region Public Methods

    /// <summary>
    /// Sets the maximum capacity of this pool.
    /// </summary>
    /// <param name="maxCapacity">The maximum capacity of this pool.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMaxCapacity(int maxCapacity)
    {
        if (maxCapacity < 0)
        {
            return;
        }

        int oldCapacity = Interlocked.Exchange(ref _maxCapacity, maxCapacity);

        // Slots are sized at construction, so growing the capacity needs a larger slot array.
        if (maxCapacity > _slots.Length)
        {
            this.GrowSlots(maxCapacity);
        }

        // If the new capacity is less than the old one, trim the pool
        if (maxCapacity < oldCapacity)
        {
            _ = this.Trim(100); // Trim to exactly the max capacity
        }
    }

    /// <summary>
    /// Replaces the slot array with a larger one, copying existing slots. Runs only on capacity increases.
    /// </summary>
    private void GrowSlots(int newSize)
    {
        lock (_growLock)
        {
            IPoolable?[] current = _slots;
            if (newSize <= current.Length)
            {
                return;
            }

            IPoolable?[] grown = new IPoolable?[newSize];
            System.Array.Copy(current, grown, current.Length);
            Volatile.Write(ref _slots, grown);
        }
    }

    /// <summary>
    /// Tries to add an object to the pool.
    /// </summary>
    /// <param name="obj">The object to add.</param>
    /// <returns>True if the object was added, false if the pool is full.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPush(IPoolable obj)
    {
        IPoolable?[] slots = _slots;
        int length = slots.Length;

        // The slot array bounds the pool: when it is full the scan finds no empty slot. Only a capacity lowered
        // below the array's length (SetMaxCapacity) needs the count, which is summed for it.
        int max = Volatile.Read(ref _maxCapacity);
        if (max < length && this.SumCounts() >= max)
        {
            return false;
        }

        for (int n = 0, i = RegionStart(length); n < length; n++, i = i + 1 == length ? 0 : i + 1)
        {
            // Read first: a CAS on an occupied slot is a locked instruction for nothing.
            if (Volatile.Read(ref slots[i]) == null && Interlocked.CompareExchange(ref slots[i], obj, null) == null)
            {
                _ = Interlocked.Increment(ref this.CurrentCount);
                return true; // Slot claimed successfully.
            }
        }

        return false; // Every slot is occupied: the pool is full.
    }

    /// <summary>
    /// Where the current processor starts scanning: its own run of eight slots (one 64-byte cache line), so threads
    /// on different cores push and pop on different lines instead of all fighting over the first slots. A scan
    /// still wraps over the whole array, so capacity and what can be found are unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RegionStart(int length) => (int)((uint)(Thread.GetCurrentProcessorId() * RegionSize) % (uint)length);

    /// <summary>
    /// Tries to get an object from the pool.
    /// </summary>
    /// <param name="obj">The object from the pool.</param>
    /// <returns>True if an object was retrieved, false if the pool is empty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out IPoolable? obj)
    {
        IPoolable?[] slots = _slots;
        int length = slots.Length;
        int start = RegionStart(length);

        // This core's own run first: a busy pool almost always has an object there.
        int region = length < RegionSize ? length : RegionSize;
        if (TryTake(slots, start, region, length, out obj))
        {
            _ = Interlocked.Decrement(ref this.CurrentCount);
            return true;
        }

        // An empty pool answers from the counts. It used to scan every slot (2048 by default) first, on every
        // Get of a type that is rented and never returned, such as the responses a client hands to its caller.
        // A push counts after it lands, so a pop racing it may miss that object: it is only a miss.
        if (region == length || this.SumCounts() <= 0)
        {
            return false;
        }

        if (TryTake(slots, start + region, length - region, length, out obj))
        {
            _ = Interlocked.Decrement(ref this.CurrentCount);
            return true;
        }

        return false;
    }

    /// <summary>Slots per processor region: eight references, one 64-byte cache line.</summary>
    private const int RegionSize = 8;

    /// <summary>Takes the first object among <paramref name="count"/> slots from <paramref name="from"/>, wrapping.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryTake(IPoolable?[] slots, int from, int count, int length, out IPoolable? obj)
    {
        for (int n = 0, i = from % length; n < count; n++, i = i + 1 == length ? 0 : i + 1)
        {
            IPoolable? candidate = Volatile.Read(ref slots[i]);
            if (candidate != null && Interlocked.CompareExchange(ref slots[i], null, candidate) == candidate)
            {
                obj = candidate;
                return true;
            }
        }

        obj = null;
        return false;
    }

    /// <summary>
    /// Clears all objects from this pool.
    /// </summary>
    /// <returns>The number of objects removed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Clear()
    {
        IPoolable?[] slots = _slots;
        int removed = 0;

        // Atomically null each slot, and count each removal, so that concurrent pushes that land between
        // individual clears are still accounted for.
        for (int i = 0; i < slots.Length; i++)
        {
            if (Interlocked.Exchange(ref slots[i], null) != null)
            {
                removed++;
                _ = Interlocked.Decrement(ref this.CurrentCount);
            }
        }

        return removed;
    }

    /// <summary>
    /// Trims the pool to a target size based on a percentage of the maximum capacity.
    /// </summary>
    /// <param name="percentage">
    /// The percentage of the maximum capacity to keep (0-100).
    /// <list type="bullet">
    ///   <item><description><c>0</c> = no trim (pool is at or below its safety floor).</description></item>
    ///   <item><description><c>1–99</c> = keep <c>percentage</c>% of <see cref="MaxCapacity"/>.</description></item>
    ///   <item><description><c>100</c> = keep up to full <see cref="MaxCapacity"/>.</description></item>
    ///   <item><description><c>&lt; 0</c> = clear all objects.</description></item>
    /// </list>
    /// </param>
    /// <param name="decayFactor">Fraction of excess objects to remove (0.0-1.0). Default is 1.0 (remove all excess).</param>
    /// <returns>The number of objects removed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public int Trim(int percentage, double decayFactor = 1.0)
    {
        if (percentage < 0)
        {
            return this.Clear();
        }

        if (percentage == 0)
        {
            return 0; // Safety floor reached — retain all available objects.
        }

        int max = Volatile.Read(ref _maxCapacity);
        int targetSize = percentage >= 100
            ? max
            : max * percentage / 100;

        int currentCount = this.SumCounts();
        if (currentCount <= targetSize)
        {
            return 0;
        }

        int excess = currentCount - targetSize;
        int toRemove = decayFactor >= 1.0
            ? excess
            : System.Math.Max(1, (int)(excess * decayFactor));

        int removed = RemoveObjects(_slots, toRemove);

        // Count each actually removed slot.
        _ = Interlocked.Add(ref this.CurrentCount, -removed);

        return removed;

        static int RemoveObjects(IPoolable?[] slots, int count)
        {
            int removed = 0;

            for (int i = 0; i < slots.Length && removed < count; i++)
            {
                if (Interlocked.Exchange(ref slots[i], null) != null)
                {
                    removed++;
                }
            }

            return removed;
        }
    }

    /// <summary>
    /// Gets the objects in this pool as an array.
    /// </summary>
    /// <remarks>This is primarily for diagnostic purposes and should not be used in performance-critical code.</remarks>
    /// <returns>An array containing the objects in this pool.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IPoolable[] ToArray()
    {
        IPoolable?[] slots = _slots;
        System.Collections.Generic.List<IPoolable> result = [];

        for (int i = 0; i < slots.Length; i++)
        {
            IPoolable? candidate = Volatile.Read(ref slots[i]);
            if (candidate != null)
            {
                result.Add(candidate);
            }
        }

        return [.. result];
    }

    #endregion Public Methods

    /// <summary>Gets the count slot of the current processor.</summary>
    private ref int CurrentCount => ref _counts[Thread.GetCurrentProcessorId() & (_counts.Length - 1)].Value;

    private int SumCounts()
    {
        int sum = 0;
        for (int i = 0; i < _counts.Length; i++)
        {
            sum += Volatile.Read(ref _counts[i].Value);
        }

        return sum;
    }

    /// <summary>A power of two of at least the processor count, capped at 64.</summary>
    private static int CountSlotCount()
        => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)System.Math.Clamp(System.Environment.ProcessorCount, 1, 64));

    /// <summary>The count of one processor, alone on a 64-byte cache line.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 64)]
    private struct CountSlot
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public int Value;
    }
}
