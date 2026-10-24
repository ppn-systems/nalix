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
    /// Preallocated fixed-slot array. Once constructed, the reference never changes;
    /// individual slots are atomically swapped between null and non-null.
    /// </summary>
    private readonly IPoolable?[] _slots = new IPoolable?[maxCapacity > 0 ? maxCapacity : 1];

    /// <summary>
    /// Conservative count of objects in the pool.
    /// <para>
    /// Incremented by <see cref="TryPush"/> before the CAS attempt (optimistic);
    /// decremented by <see cref="TryPop"/> after a successful slot exchange.
    /// This means the count can temporarily exceed the true number of non-null slots
    /// (e.g., when a push fails its CAS and decrements) or briefly dip negative
    /// under concurrent pop/clear races. External consumers should treat
    /// <see cref="AvailableCount"/> as a best-effort approximation.
    /// </para>
    /// </summary>
    private int _count;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets the number of objects available in this pool.
    /// </summary>
    public int AvailableCount => System.Math.Max(0, Volatile.Read(ref _count));

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

        // If the new capacity is less than the old one, trim the pool
        if (maxCapacity < oldCapacity)
        {
            _ = this.Trim(100); // Trim to exactly the max capacity
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
        int max = Volatile.Read(ref _maxCapacity);
        IPoolable?[] slots = _slots;

        // Pessimistic capacity gate: if we are already at or over capacity, reject immediately.
        if (Volatile.Read(ref _count) >= max)
        {
            return false;
        }

        // Optimistic increment: if we are still under capacity after the increment, proceed with CAS.
        if (Interlocked.Increment(ref _count) <= max)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (Interlocked.CompareExchange(ref slots[i], obj, null) == null)
                {
                    return true; // Slot claimed successfully.
                }
            }
        }

        // Pool is full or all slots are occupied — undo the optimistic increment.
        _ = Interlocked.Decrement(ref _count);
        return false;
    }

    /// <summary>
    /// Tries to get an object from the pool.
    /// </summary>
    /// <param name="obj">The object from the pool.</param>
    /// <returns>True if an object was retrieved, false if the pool is empty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out IPoolable? obj)
    {
        IPoolable?[] slots = _slots;

        for (int i = 0; i < slots.Length; i++)
        {
            IPoolable? candidate = Volatile.Read(ref slots[i]);
            if (candidate != null &&
                Interlocked.CompareExchange(ref slots[i], null, candidate) == candidate)
            {
                obj = candidate;
                _ = Interlocked.Decrement(ref _count);
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

        // Atomically null each slot. Decrement _count per removed slot so that
        // concurrent TryPush increments that land between individual clears are
        // still accounted for.
        for (int i = 0; i < slots.Length; i++)
        {
            if (Interlocked.Exchange(ref slots[i], null) != null)
            {
                removed++;
                _ = Interlocked.Decrement(ref _count);
            }
        }

        // Clamp to zero to handle any transient negative value from concurrent
        // TryPop that also decremented _count for a slot we already cleared.
        if (Volatile.Read(ref _count) < 0)
        {
            Volatile.Write(ref _count, 0);
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

        int currentCount = Volatile.Read(ref _count);
        if (currentCount <= targetSize)
        {
            return 0;
        }

        int excess = currentCount - targetSize;
        int toRemove = decayFactor >= 1.0
            ? excess
            : System.Math.Max(1, (int)(excess * decayFactor));

        int removed = RemoveObjects(_slots, toRemove);

        // Decrement _count for each actually removed slot.  Then clamp to zero
        // to absorb any transient negative value from concurrent TryPop operations.
        _ = Interlocked.Add(ref _count, -removed);
        if (Volatile.Read(ref _count) < 0)
        {
            Volatile.Write(ref _count, 0);
        }

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
}

