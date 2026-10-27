// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Network.Internal.Pooling;

/// <summary>
/// Represents a small, connection-scoped object pool optimized for fast, lock-free access.
///
/// <para>
/// This pool acts as a <b>local cache layer</b> on top of a global <see cref="ObjectPoolManager"/>.
/// It minimizes contention and allocations by keeping a fixed-size array of reusable objects
/// bound to a specific connection.
/// </para>
///
/// <para>
/// <b>Design characteristics:</b>
/// <list type="bullet">
/// <item><description>Fixed-size pool (2 slots) using a bitmask for tracking usage.</description></item>
/// <item><description>Lock-free acquisition using <see cref="Interlocked"/> operations.</description></item>
/// <item><description>Safe fallback to global pool when local pool is unavailable or destroyed.</description></item>
/// <item><description>No object-level awareness of pool ownership (pool is externally managed).</description></item>
/// </list>
/// </para>
/// </summary>
/// <typeparam name="T">
/// The pooled object type. Must implement <see cref="IPoolable"/> and provide a parameterless constructor.
/// </typeparam>
internal struct LocalPool<T> where T : class, IPoolable, new()
{
    /// <summary>
    /// The fixed number of slots in the local pool.
    /// </summary>
    private const int Size = 2;

    /// <summary>
    /// Bit 63 represents whether the pool is destroyed.
    /// </summary>
    private const long DestroyedBit = 1L << 63;

    /// <summary>
    /// Reference to the global pool manager used as a fallback and source of objects.
    /// </summary>
    private readonly ObjectPoolManager _globalPool;

    /// <summary>
    /// Backing storage for pooled items. Null indicates the pool has not been initialized
    /// or has been destroyed.
    /// </summary>
    private T[]? _items;

    /// <summary>
    /// Bitmask representing slot usage:
    /// 1 = occupied (busy), 0 = available (free).
    /// </summary>
    private long _mask;

    /// <summary>
    /// Flag indicating whether the pool has been destroyed.
    /// 0 = active, 1 = destroyed.
    /// </summary>
    private int _destroyed;

    /// <summary>
    /// Flag for CAS initialization lock:
    /// 0 = uninitialized, 1 = initializing, 2 = initialized.
    /// </summary>
    private int _initLock;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalPool{T}"/> struct.
    /// </summary>
    /// <param name="globalPool">The global pool manager used for fallback operations.</param>
    public LocalPool(ObjectPoolManager globalPool)
    {
        _globalPool = globalPool;
        _items = null;
        _mask = 0;
        _destroyed = 0;
        _initLock = 0;
    }

    /// <summary>
    /// Attempts to acquire an object from the local pool with a custom state argument
    /// passed to the initializer to avoid closures/allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Acquire<TState>(TState state, System.Action<T, TState> initialize)
    {
        if (Volatile.Read(ref _destroyed) != 0)
        {
            return null;
        }

        this.EnsureInitialized(state, initialize);

        T[]? items = _items;
        if (items == null)
        {
            return null;
        }
        for (int i = 0; i < Size; i++)
        {
            long bit = 1L << i;
            long oldMask;
            long newMask;
            bool success = false;

            do
            {
                oldMask = Volatile.Read(ref _mask);
                if ((oldMask & DestroyedBit) != 0)
                {
                    return null;
                }

                if ((oldMask & bit) != 0)
                {
                    break; // Slot is busy, try next slot
                }

                newMask = oldMask | bit;
                success = Interlocked.CompareExchange(ref _mask, newMask, oldMask) == oldMask;
            } while (!success);

            if (success)
            {
                T item = items[i];

                if (item is IPoolRentable rentable)
                {
                    rentable.OnRent();
                }

                return item;
            }
        }

        // All slots are busy
        return null;
    }

    /// <summary>
    /// Returns an object back to the pool.
    /// </summary>
    /// <param name="item">The object to return.</param>
    /// <remarks>
    /// If the local pool is still active and owns the object, it will be returned to its slot.
    /// Otherwise, the object is safely returned to the global pool.
    ///
    /// This guarantees that objects are never leaked, even after pool destruction.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(T item)
    {
        T[]? items = _items;

        if (items != null && Volatile.Read(ref _destroyed) == 0)
        {
            for (int i = 0; i < Size; i++)
            {
                if (ReferenceEquals(items[i], item))
                {
                    item.ResetForPool();

                    long bit = 1L << i;
                    long oldMask;
                    long newMask;
                    bool success;
                    do
                    {
                        oldMask = Volatile.Read(ref _mask);
                        if ((oldMask & DestroyedBit) != 0)
                        {
                            success = false;
                            break;
                        }
                        newMask = oldMask & ~bit;
                        success = Interlocked.CompareExchange(ref _mask, newMask, oldMask) == oldMask;
                    } while (!success);

                    if (success)
                    {
                        return;
                    }

                    break;
                }
            }
        }

        // Pool destroyed or foreign object → fallback to global pool
        item.ResetForPool();
        _globalPool.Return(item);
    }

    /// <summary>
    /// Destroys the local pool and releases its resources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is typically called when the associated connection is disposed.
    /// </para>
    ///
    /// <para>
    /// Behavior:
    /// <list type="bullet">
    /// <item><description>Idle objects are immediately returned to the global pool.</description></item>
    /// <item><description>
    /// Busy objects are not forcibly reclaimed. Instead, they will automatically
    /// fall back to the global pool when <see cref="Return(T)"/> is called.
    /// </description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// After destruction, the local pool becomes inactive and all operations will
    /// transparently use the global pool.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Destroy()
    {
        long oldMask;
        do
        {
            oldMask = Volatile.Read(ref _mask);
            if ((oldMask & DestroyedBit) != 0)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _mask, oldMask | DestroyedBit, oldMask) != oldMask);

        Volatile.Write(ref _destroyed, 1);

        T[]? items = Interlocked.Exchange(ref _items, null);
        if (items == null)
        {
            return;
        }

        for (int i = 0; i < Size; i++)
        {
            T? item = items[i];
            if (item == null)
            {
                continue;
            }

            bool wasIdle = (oldMask & (1L << i)) == 0;

            if (wasIdle)
            {
                // Immediately return idle items to global pool
                item.ResetForPool();
                _globalPool.Return(item);
            }

            // Busy items will fallback to global pool upon Return()
        }

        // The slot array is intentionally NOT returned to ArrayPool: an Acquire() that won its slot CAS
        // just before Destroy() may still read items[i], and a recycled array would be cleared or
        // refilled by another connection's pool.
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInitialized<TState>(TState state, System.Action<T, TState> initialize)
    {
        if (Volatile.Read(ref _initLock) == 2 || Volatile.Read(ref _destroyed) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _initLock, 1, 0) != 0)
        {
            SpinWait spin = default;
            while (Volatile.Read(ref _initLock) == 1)
            {
                spin.SpinOnce();
            }
            return;
        }

        T[]? arr = null;
        try
        {
            arr = new T[Size];
            for (int i = 0; i < Size; i++)
            {
                arr[i] = _globalPool.Get<T>();
                initialize(arr[i], state);
            }
            _items = arr;
            Volatile.Write(ref _initLock, 2);
        }
        catch
        {
            if (arr != null)
            {
                for (int i = 0; i < Size; i++)
                {
                    if (arr[i] != null)
                    {
                        arr[i].ResetForPool();
                        _globalPool.Return(arr[i]);
                    }
                }
            }
            Volatile.Write(ref _initLock, 0);
            throw;
        }
    }
}
