// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Framework.Memory.Internal.PoolTypes;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Framework.Memory.Pools;

/// <summary>
/// A thread-safe pool that stores and reuses <see cref="IPoolable"/> instances by type.
/// Objects are reset before being returned to the pool so callers always receive a
/// clean instance on the next rent.
/// </summary>
/// <remarks>
/// Each pooled type gets its own internal bucket and capacity limit. The pool is
/// intentionally simple: rent fast, reset on return, and discard when full.
/// </remarks>
/// <param name="defaultMaxItemsPerType">The default maximum number of items to keep per pooled type.</param>
/// <param name="threadCacheDepth">
/// Maximum thread-local slots per type. <c>0</c> (default) disables thread-local caching.
/// Keep at <c>0</c> for async/await workloads to prevent object stranding on idle threads.
/// </param>
public sealed class ObjectPool(int defaultMaxItemsPerType, int threadCacheDepth = 0)
{
    #region Constants

    /// <summary>
    /// Standard maximum pool size used when the caller does not provide a positive limit.
    /// </summary>
    public const int DefaultMaxSize = 1024;

    #endregion Constants

    #region Fields

    /// <summary>
    /// Type-specific storage for pooled objects.
    /// Each concrete type gets its own bucket so instances never cross type boundaries.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, TypePool> _typePools = new();
    private TypePool?[] _typePoolsArray = new TypePool?[64];

    /// <summary>
    /// Statistics tracking for diagnostics and capacity tuning.
    /// </summary>
    private long _totalCreated;

    private long _totalReturned;
    private long _totalRented;
    private long _totalDropped;
    private long _totalRejectedReturns;
    private readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Configuration for the default per-type pool capacity.
    /// </summary>
    private readonly int _defaultMaxItemsPerType = defaultMaxItemsPerType > 0 ? defaultMaxItemsPerType : DefaultMaxSize;

    /// <summary>
    /// Maximum thread-local slots per type. <c>0</c> disables thread-local caching entirely.
    /// </summary>
    private readonly int _threadCacheDepth = threadCacheDepth;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets the singleton instance of the object pool.
    /// </summary>
    public static ObjectPool Default { get; } = new();

    /// <summary>
    /// Gets the total number of objects created across all pooled types.
    /// </summary>
    public long TotalCreatedCount
    {
        get
        {
            Thread.MemoryBarrier();
            return Volatile.Read(ref _totalCreated);
        }
    }

    /// <summary>
    /// Gets the total number of objects currently available across all pools.
    /// </summary>
    public int TotalAvailableCount
    {
        get
        {
            int count = 0;
            foreach (TypePool pool in _typePools.Values)
            {
                count += pool.AvailableCount;
            }
            return count;
        }
    }

    /// <summary>
    /// Gets the number of distinct object types currently being pooled.
    /// </summary>
    public int TypeCount => _typePools.Count;

    /// <summary>
    /// Gets the total number of objects returned to the pool.
    /// </summary>
    public long TotalReturnedCount => Interlocked.Read(ref _totalReturned);

    /// <summary>
    /// Gets the total number of objects rented from the pool.
    /// </summary>
    public long TotalRentedCount => Interlocked.Read(ref _totalRented);

    /// <summary>
    /// Gets the total number of objects dropped (discarded) because the pool was full.
    /// </summary>
    public long TotalDroppedCount => Interlocked.Read(ref _totalDropped);

    /// <summary>
    /// Gets the total number of duplicate returns rejected by pool state tracking.
    /// </summary>
    public long TotalRejectedReturnCount => Interlocked.Read(ref _totalRejectedReturns);

    /// <summary>
    /// Gets the pool uptime in milliseconds.
    /// </summary>
    public long UptimeMs => _uptime.ElapsedMilliseconds;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectPool"/> class with default settings.
    /// </summary>
    public ObjectPool() : this(DefaultMaxSize)
    {
    }

    #endregion Constructor

    #region Public Methods

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal (T obj, bool isCacheHit) GetWithInfoFast<T>(int id) where T : IPoolable, new()
    {
        // Try the fast-path thread-local slot first (only when enabled)
        if (_threadCacheDepth > 0)
        {
            T? localObj = ThreadLocalCache<T>.TryPop(this);
            if (localObj != null)
            {
                MarkRented(localObj);
                _ = Interlocked.Increment(ref _totalRented);
                return (localObj, true);
            }
        }

        /*
         * [Type-Sharded Retrieval]
         * We resolve the bucket for this specific type. Each type has its 
         * own lock-free stack of available instances.
         */
        TypePool? typePool = id < _typePoolsArray.Length ? _typePoolsArray[id] : null;
        typePool ??= this.InitializeTypePoolFast<T>(id);

        // Rent from the bucket when possible; otherwise create a fresh instance.
        if (typePool.TryPop(out IPoolable? obj) && obj != null)
        {
            MarkRented(obj);
            _ = Interlocked.Increment(ref _totalRented);
            return ((T)obj, true);
        }

        // Pool miss: create a new instance and account for it as a fresh allocation.
        T newObj = new();
        MarkRented(newObj);

        _ = Interlocked.Increment(ref _totalCreated);
        _ = Interlocked.Increment(ref _totalRented);
        return (newObj, false);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool ReturnFast<T>(T obj, int id) where T : IPoolable
    {
        if (!TryMarkReturned(obj))
        {
            _ = Interlocked.Increment(ref _totalRejectedReturns);
            return false;
        }

        obj.ResetForPool();

        TypePool? typePool = id < _typePoolsArray.Length ? _typePoolsArray[id] : null;
        typePool ??= this.InitializeTypePoolFast<T>(id);

        // Prevent thread-local hoarding by ensuring the central pool isn't starved.
        // If it's empty, prioritize pushing to the central pool so other threads can use it.
        if (typePool.AvailableCount == 0 && typePool.TryPush(obj))
        {
            _ = Interlocked.Increment(ref _totalReturned);
            return true;
        }

        // Try the fast-path thread-local slot (only when enabled)
        if (_threadCacheDepth > 0 && ThreadLocalCache<T>.TryPush(this, obj))
        {
            _ = Interlocked.Increment(ref _totalReturned);
            return true;
        }

        // Fallback to central pool if thread-local slot is occupied or disabled
        if (typePool.TryPush(obj))
        {
            _ = Interlocked.Increment(ref _totalReturned);
            return true;
        }

        // Object was dropped because the pool is at MaxCapacity
        _ = Interlocked.Increment(ref _totalDropped);
        return false;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private TypePool InitializeTypePoolFast<T>(int id) where T : IPoolable
    {
        TypePool typePool = _typePools.GetOrAdd(typeof(T), _ => new TypePool(_defaultMaxItemsPerType));

        lock (_typePools)
        {
            if (id >= _typePoolsArray.Length)
            {
                int newSize = Math.Max(id + 1, _typePoolsArray.Length * 2);
                Array.Resize(ref _typePoolsArray, newSize);
            }

            _typePoolsArray[id] = typePool;
        }

        return typePool;
    }

    /// <summary>
    /// Gets an object from the pool and returns whether it was a cache hit (reused from pool) or miss (newly created).
    /// This is the single source of truth for hit/miss counting → eliminates TOCTOU in ObjectPoolManager.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public (T obj, bool isCacheHit) GetWithInfo<T>() where T : IPoolable, new()
    {
        int id = PoolType<T>.Id;
        return this.GetWithInfoFast<T>(id);
    }

    /// <summary>
    /// Gets an instance of <typeparamref name="T"/>, creating a new one when the pool is empty.
    /// </summary>
    /// <typeparam name="T">The type of object to get from the pool.</typeparam>
    /// <returns>An instance of <typeparamref name="T"/>.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    [return: System.Diagnostics.CodeAnalysis.NotNull]
    public T Get<T>() where T : IPoolable, new()
    {
        int id = PoolType<T>.Id;
        return this.GetWithInfoFast<T>(id).obj;
    }

    /// <summary>
    /// Returns an instance of <typeparamref name="T"/> to the pool for future reuse.
    /// </summary>
    /// <typeparam name="T">The type of object to return to the pool.</typeparam>
    /// <param name="obj">The object to return to the pool.</param>
    /// <exception cref="ArgumentNullException">Thrown when obj is null.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Return<T>(T obj) where T : IPoolable
    {
        /*
         * [Reset Lifecycle]
         * Before returning an object to the pool, we MUST call ResetForPool().
         * This ensures that the next consumer doesn't see stale state from 
         * the previous owner.
         */
        if (EqualityComparer<T>.Default.Equals(obj, default))
        {
            THROW_NULL_OBJECT();
        }

        int id = PoolType<T>.Id;
        _ = this.ReturnFast(obj, id);
    }

    /// <summary>
    /// Preallocates and stores multiple new instances of <typeparamref name="T"/> in the pool.
    /// </summary>
    /// <typeparam name="T">The type of objects to preallocate.</typeparam>
    /// <param name="count">The number of instances to preallocate.</param>
    /// <returns>The number of instances successfully added to the pool.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int Prealloc<T>(int count) where T : IPoolable, new()
    {
        if (count <= 0)
        {
            return 0;
        }

        Type type = typeof(T);
        TypePool typePool = _typePools.GetOrAdd(type, _ => new TypePool(_defaultMaxItemsPerType));

        int created = 0;
        for (int i = 0; i < count; i++)
        {
            // Preallocation stops as soon as the bucket reports that it is full.
            T obj = new();
            _ = TryMarkReturned(obj);
            if (typePool.TryPush(obj))
            {
                created++;
                _ = Interlocked.Increment(ref _totalCreated);
                _ = Interlocked.Increment(ref _totalReturned);
            }
            else
            {
                // Stop once capacity is reached so preallocation does not overshoot the limit.
                break;
            }
        }

        return created;
    }

    /// <summary>
    /// Sets the maximum capacity for a specific type's pool.
    /// </summary>
    /// <typeparam name="T">The type to configure.</typeparam>
    /// <param name="maxCapacity">The maximum capacity for the type's pool.</param>
    /// <returns>True if the capacity was set, false otherwise.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool SetMaxCapacity<T>(int maxCapacity) where T : IPoolable
    {
        if (maxCapacity < 0)
        {
            return false;
        }

        // GetOrAdd keeps a concurrently created TypePool (and objects already in it) instead of overwriting it.
        TypePool typePool = _typePools.GetOrAdd(typeof(T), static (_, capacity) => new TypePool(capacity), maxCapacity);
        typePool.SetMaxCapacity(maxCapacity);
        return true;
    }

    /// <summary>
    /// Gets information about a specific type's pool.
    /// </summary>
    /// <typeparam name="T">The type to get information about.</typeparam>
    /// <returns>A dictionary containing pool statistics for the type.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public Dictionary<string, object> GetTypeInfo<T>() where T : IPoolable
    {
        Type type = typeof(T);
        return _typePools.TryGetValue(type, out TypePool? typePool)
            ? CREATE_TYPE_INFO(type.Name, typePool.AvailableCount, typePool.MaxCapacity, true)
            : CREATE_TYPE_INFO(type.Name, 0, _defaultMaxItemsPerType, false);
    }

    /// <summary>
    /// Gets statistics about the pool's usage.
    /// </summary>
    /// <returns>A dictionary containing statistics about the pool.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public Dictionary<string, object> GetStatistics()
    {
        return new Dictionary<string, object>(8, StringComparer.Ordinal)
        {
            ["TotalCreatedCount"] = this.TotalCreatedCount,
            ["TotalAvailableCount"] = this.TotalAvailableCount,
            ["TypeCount"] = this.TypeCount,
            ["TotalRentedCount"] = this.TotalRentedCount,
            ["TotalReturnedCount"] = this.TotalReturnedCount,
            ["TotalDroppedCount"] = this.TotalDroppedCount,
            ["TotalRejectedReturnCount"] = this.TotalRejectedReturnCount,
            ["ActiveRentals"] = this.TotalRentedCount - this.TotalReturnedCount,
            ["UptimeMs"] = this.UptimeMs,
            ["DefaultMaxItemsPerType"] = _defaultMaxItemsPerType
        };
    }

    /// <summary>
    /// Clears all objects from the pool.
    /// </summary>
    /// <returns>The total number of objects removed.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int Clear()
    {
        int removedCount = 0;
        foreach (TypePool pool in _typePools.Values)
        {
            removedCount += pool.Clear();
        }

        return removedCount;
    }

    /// <summary>
    /// Clears a specific type's pool.
    /// </summary>
    /// <typeparam name="T">The type to clear from the pool.</typeparam>
    /// <returns>The number of objects removed.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int ClearType<T>() where T : IPoolable
    {
        Type type = typeof(T);
        if (_typePools.TryGetValue(type, out TypePool? typePool))
        {
            int removedCount = typePool.Clear();
            return removedCount;
        }

        return 0;
    }

    /// <summary>
    /// Trims all type pools to their target sizes.
    /// </summary>
    /// <param name="percentage">
    /// The percentage of the maximum capacity to keep (0-100).
    /// <c>0</c> = no trim (safety floor), <c>1–99</c> = keep that percentage of max capacity,
    /// <c>100</c> = keep up to full capacity. Negative values are clamped to <c>0</c>.
    /// </param>
    /// <param name="decayFactor">Fraction of excess objects to remove (0.0-1.0). Default is 1.0 (remove all excess).</param>
    /// <returns>The total number of objects removed.</returns>
    public int Trim(int percentage = 50, double decayFactor = 1.0)
    {
        if (percentage < 0)
        {
            percentage = 0; // Negative → no trim (safety floor semantics)
        }

        if (percentage > 100)
        {
            percentage = 100;
        }

        int removedCount = 0;
        foreach (TypePool pool in _typePools.Values)
        {
            removedCount += pool.Trim(percentage, decayFactor);
        }

        return removedCount;
    }

    /// <summary>
    /// Resets the statistics of the pool.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void ResetStatistics()
    {
        _ = Interlocked.Exchange(ref _totalCreated, 0);
        _ = Interlocked.Exchange(ref _totalRented, 0);
        _ = Interlocked.Exchange(ref _totalReturned, 0);
        _ = Interlocked.Exchange(ref _totalDropped, 0);
        _ = Interlocked.Exchange(ref _totalRejectedReturns, 0);
        _uptime.Restart();

    }

    /// <summary>
    /// Gets information about all type pools.
    /// </summary>
    /// <returns>A list of dictionaries containing information about each type pool.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public List<
        Dictionary<string, object>> GetAllTypeInfo()
    {
        List<
            Dictionary<string, object>> result = new(_typePools.Count);

        foreach (KeyValuePair<Type, TypePool> kvp in _typePools)
        {
            TypePool typePool = kvp.Value;
            result.Add(CREATE_TYPE_INFO(kvp.Key.Name, typePool.AvailableCount, typePool.MaxCapacity, true));
        }

        return result;
    }

    /// <summary>
    /// Batch returns multiple objects to the pool.
    /// </summary>
    /// <typeparam name="T">The type of objects to return.</typeparam>
    /// <param name="objects">The objects to return to the pool.</param>
    /// <returns>The number of objects successfully returned to the pool.</returns>
    /// <exception cref="ArgumentNullException">Thrown when objects is null.</exception>
    public int ReturnMultiple<T>(IEnumerable<T> objects) where T : IPoolable
    {
        ArgumentNullException.ThrowIfNull(objects);

        int returnedCount = 0;
        Type type = typeof(T);
        TypePool typePool = _typePools.GetOrAdd(type, _ => new TypePool(_defaultMaxItemsPerType));

        foreach (T obj in objects)
        {
            if (EqualityComparer<T>.Default.Equals(obj, default))
            {
                continue;
            }

            if (!TryMarkReturned(obj))
            {
                _ = Interlocked.Increment(ref _totalRejectedReturns);
                continue;
            }

            obj.ResetForPool();

            if (typePool.TryPush(obj))
            {
                returnedCount++;
                _ = Interlocked.Increment(ref _totalReturned);
            }
        }

        return returnedCount;
    }

    /// <summary>
    /// Gets multiple objects from the pool at once.
    /// </summary>
    /// <typeparam name="T">The type of objects to get.</typeparam>
    /// <param name="count">The number of objects to get.</param>
    /// <returns>A list containing the requested objects.</returns>
    /// <exception cref="ArgumentException">Thrown when count is less than or equal to zero.</exception>
    public List<T> GetMultiple<T>(int count) where T : IPoolable, new()
    {
        if (count <= 0)
        {
            throw new ArgumentException("Count must be greater than zero.", nameof(count));
        }

        List<T> result = new(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                result.Add(this.Get<T>());
            }
            return result;
        }
        catch
        {
            // SEC-88: Return already acquired objects to the pool if an exception occurs
            // to prevent resource leaks.
            _ = this.ReturnMultiple(result);
            throw;
        }
    }

    /// <summary>
    /// Creates a new type-specific pool for more efficient operations without runtime type checking.
    /// </summary>
    /// <typeparam name="T">The type of objects to manage in the pool.</typeparam>
    /// <returns>A type-specific pool for <typeparamref name="T"/>.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public TypedObjectPool<T> CreateTypedPool<T>() where T : IPoolable, new() => new(this);

    internal int AvailableCountByType(Type type) => _typePools.TryGetValue(type, out TypePool? typePool) ? typePool.AvailableCount : 0;

    internal int GetMaxCapacity(Type type) => _typePools.TryGetValue(type, out TypePool? typePool) ? typePool.MaxCapacity : _defaultMaxItemsPerType;

    internal Dictionary<string, object> GetTypeInfoByType(Type type)
    {
        return _typePools.TryGetValue(type, out TypePool? typePool)
            ? CREATE_TYPE_INFO(type.Name, typePool.AvailableCount, typePool.MaxCapacity, true)
            : CREATE_TYPE_INFO(type.Name, 0, _defaultMaxItemsPerType, false);
    }

    #endregion Public Methods

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Dictionary<string, object> CREATE_TYPE_INFO(string typeName, int availableCount, int maxCapacity, bool isActive)
    {
        return new Dictionary<string, object>(4, StringComparer.Ordinal)
        {
            ["TypeName"] = typeName,
            ["AvailableCount"] = availableCount,
            ["MaxCapacity"] = maxCapacity,
            ["IsActive"] = isActive
        };
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void MarkRented<T>(T obj) where T : IPoolable
    {
        if (obj is IPoolStateTracked tracked)
        {
            Volatile.Write(ref tracked.PoolState, 0);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool TryMarkReturned<T>(T obj) where T : IPoolable
    {
        if (obj is not IPoolStateTracked tracked)
        {
            return true;
        }

        return Interlocked.CompareExchange(ref tracked.PoolState, 1, 0) == 0;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void THROW_NULL_OBJECT() => throw new ArgumentNullException("obj");
}
