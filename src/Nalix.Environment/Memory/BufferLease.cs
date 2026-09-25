// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Configuration;
using Nalix.Environment.Options;

namespace Nalix.Environment.Memory;

/// <summary>
/// Represents an owned lease of a pooled byte[] rented from <see cref="IBufferPoolManager"/>.
/// Supports slice ownership (start + length) to enable zero-copy handoffs.
/// </summary>
[DebuggerNonUserCode]
[DebuggerDisplay("BufferLease Start={_start}, Len={Length}, Cap={Capacity}, Detached={_detached != 0}")]
public sealed class BufferLease : IBufferLease, IPoolable, IPoolRentable
{
    #region Static

    private sealed class ThreadLocalLeaseCache
    {
        public readonly int MaxSlots;
        public readonly BufferLease?[] Slots;
        public int Count;

        public ThreadLocalLeaseCache(int maxSlots)
        {
            MaxSlots = maxSlots;
            Slots = new BufferLease?[maxSlots];
        }
    }

    [ThreadStatic]
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "<Pending>")]
    private static ThreadLocalLeaseCache? t_localCache;

    private static readonly int s_sharedPoolSize;
    private static readonly int s_sharedPoolMask;
    private static readonly int s_threadLocalMaxSlots;
    private static readonly BufferLease?[] s_sharedPool;

    /// <summary>
    /// Gets or sets whether BufferLease pooling is enabled. Default is true.
    /// Primarily used for testing to avoid A-B-A reuse issues.
    /// </summary>
    internal static bool IsPoolingEnabled { get; set; } = true;

    /// <summary>
    /// Optional external pool manager for shell recycling.
    /// When configured, <see cref="RENT_LEASE_SHELL"/> and <see cref="Dispose"/> delegate to this manager
    /// instead of the built-in thread-local / shared pool.
    /// </summary>
    internal static IObjectPoolManager? ShellPoolManager;

    /// <summary>
    /// Configures an external pool manager for BufferLease shell recycling.
    /// When set, shells are rented/returned via the manager instead of the built-in pool.
    /// </summary>
    /// <param name="manager">The pool manager to use for shell lifecycle.</param>
    public static void Configure(IObjectPoolManager manager) => Volatile.Write(ref ShellPoolManager, manager);

    /// <summary>
    /// Pops a lease shell from the free-list (or creates a new one).
    /// Maintains the atomic free-list count for O(1) capacity checks.
    /// </summary>
    /*
     * [Lease Shell Pooling]
     * BufferLease itself is an object. To avoid GC pressure from millions 
     * of lease allocations, we pool the 'shells' (the BufferLease instances).
     * This makes BufferLease essentially allocation-free when rented.
     */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static BufferLease RENT_LEASE_SHELL()
    {
        // Fast path: delegate to configured ObjectPoolManager
        IObjectPoolManager? mgr = Volatile.Read(ref ShellPoolManager);
        if (mgr is not null)
        {
            return mgr.Get<BufferLease>();
        }

        if (!IsPoolingEnabled)
        {
            return new BufferLease();
        }

        // 1. Try thread-local cache
        ThreadLocalLeaseCache? cache = t_localCache;
        if (cache is not null && cache.Count > 0)
        {
            int idx = --cache.Count;
            BufferLease? lease = cache.Slots[idx];
            cache.Slots[idx] = null;
            if (lease is not null)
            {
                return lease;
            }
        }

        // 2. Try shared pool with striped index
        int mask = s_sharedPoolMask;
        int startIdx = System.Environment.CurrentManagedThreadId & mask;

        for (int i = 0; i < s_sharedPoolSize; i++)
        {
            int idx = (startIdx + i) & mask;
            BufferLease? lease = s_sharedPool[idx];
            if (lease is not null && Interlocked.Exchange(ref s_sharedPool[idx], null) is { } rented)
            {
                return rented;
            }
        }

        // 3. Fallback to allocation
        return new BufferLease();
    }

    /// <summary>
    /// Provides a centralized buffer pooling abstraction.
    /// Falls back to <see cref="System.Buffers.ArrayPool{T}.Shared"/> if no <see cref="IBufferPoolManager"/> is registered.
    /// </summary>
    /// <remarks>
    /// This class is optimized for high-performance scenarios by resolving the underlying pool implementation once
    /// during static initialization, avoiding runtime branching on each call.
    /// </remarks>
    public static class ByteArrayPool
    {
        private static Func<int, byte[]> s_rentFunc = System.Buffers.ArrayPool<byte>.Shared.Rent;
        private static Action<byte[], bool> s_returnFunc = System.Buffers.ArrayPool<byte>.Shared.Return;

        /// <summary>
        /// Configures the pooled backend. Call during startup to bind the managed slab pool.
        /// </summary>
        public static void Configure(IBufferPoolManager manager)
        {
            ArgumentNullException.ThrowIfNull(manager);

            Volatile.Write(ref s_rentFunc, manager.Rent);
            Volatile.Write(ref s_returnFunc, manager.Return);
        }

        /// <summary>Rents a raw buffer (fallback / legacy callers).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Rent(int capacity = 256) => Volatile.Read(ref s_rentFunc)(capacity);

        /// <summary>Returns a raw buffer to the pool <b>without</b> clearing it.</summary>
        /// <remarks>
        /// <para>
        /// Security: pooled arrays are not scrubbed on every return. Clearing the whole
        /// power-of-two array on every send/receive was a measurable hot-path cost, and most
        /// buffers only ever hold ciphertext or data that was plaintext on the wire anyway.
        /// Buffers that hold secrets (key material, or the plaintext of an encrypted frame) must
        /// be cleared by their owner: use <see cref="Return(byte[], bool)"/> with
        /// <c>clearArray: true</c>, clear the used range before returning, or rent the lease with
        /// <c>zeroOnDispose: true</c> / set <see cref="ZeroOnDispose"/>. The codec pipeline does this
        /// for decrypted frames, pre-encryption source frames and its crypto scratch regions.
        /// </para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Return(byte[] array) => Volatile.Read(ref s_returnFunc)(array, false);

        /// <summary>Returns a raw buffer to the pool, optionally clearing the whole array first.</summary>
        /// <param name="array">The buffer to return.</param>
        /// <param name="clearArray">Whether to clear the entire array before it goes back to the pool.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Return(byte[] array, bool clearArray) => Volatile.Read(ref s_returnFunc)(array, clearArray);

    }

    /// <summary>
    /// Maximum buffer size for stack allocation in <see cref="CopyFrom"/>. Larger buffers will be heap-allocated.
    /// </summary>
    public static readonly int StackAllocThreshold = 512;

    /// <summary>
    /// Headroom reserved in front of outbound frames so a stream transport can prepend its
    /// 2-byte length prefix in place instead of renting and copying a new buffer.
    /// </summary>
    public const int TransportHeadroom = 2;

    #endregion Static

    #region Fields

#if DEBUG
    private const byte PoisonByte = 0xCD;
    private const bool EnablePoisonOnDispose = true;
#endif

    /// <summary>slice start (≥ 0, ≤ RawCapacity)</summary>
    private int _start;

    /// <summary>
    /// Bytes directly before <c>_start</c> that were reserved by <see cref="Rent(int, bool, int)"/>
    /// for a transport header and are not part of the payload (0 when none were reserved).
    /// </summary>
    private int _headroom;

    /// <summary>reference count (≥ 0)</summary>
    private int _refCount;

    /// <summary>0 = not detached, 1 = detached</summary>
    private int _detached;

    private byte[]? _buffer;

    /// <summary>
    /// Indicates whether this packet was rented from a pool and should be returned on disposal.
    /// 1 = Rented, 0 = Available/Returned.
    /// </summary>
    private int _isRented;

    #endregion Fields

    #region Constructors

    static BufferLease()
    {
        MemoryOptions options = ConfigurationManager.Instance.Get<MemoryOptions>();

        int poolSize = options.BufferLeaseSharedPoolSize;
        s_threadLocalMaxSlots = options.BufferLeaseThreadLocalCacheMaxSlots;

        s_sharedPoolSize = poolSize;
        s_sharedPoolMask = poolSize - 1;
        s_sharedPool = new BufferLease?[poolSize];
    }

    /// <summary>
    /// Parameterless constructor for free-list reuse.
    /// Do not call directly — use <see cref="Rent(int, bool)"/>, <see cref="CopyFrom"/>, or <see cref="TakeOwnership"/>.
    /// </summary>
    public BufferLease() { }

    private void Initialize(byte[] buffer, int start, int length, bool zeroOnDispose)
    {
        if ((uint)start > (uint)buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if ((uint)length > (uint)(buffer.Length - start))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _refCount = 1;
        _detached = 0;
        _start = start;
        _headroom = 0;
        _buffer = buffer;

        this.Length = length;
        this.ZeroOnDispose = zeroOnDispose;
    }

    /// <summary>
    /// Satisfies <see cref="IPoolable"/> contract. Internal pooling uses the free-list path;
    /// external callers should not need to call this directly.
    /// </summary>
    public void ResetForPool()
    {
        _buffer = null;
        _start = 0;
        _headroom = 0;
        _refCount = 0;
        _detached = 0;
        this.Length = 0;
        this.ZeroOnDispose = false;
        this.IsReliable = false;
        this.EncryptedOnWire = false;
    }

#if DEBUG
    /// <summary>
    /// Finalizer for debugging aid — detects if a lease is discarded without being released.
    /// Actual pool-wide leak detection is handled by BufferPoolManager.
    /// </summary>
    ~BufferLease()
    {
        if (_buffer != null && Volatile.Read(ref _detached) == 0)
        {
            // Optional: Log lease-level discard, but core leak detection is now in the pool.
            // _logger?.Warn($"BufferLease of size {_buffer.Length} discarded without Dispose.");
        }
    }
#endif

    #endregion Constructors

    #region Properties

    /// <summary>
    /// Gets or sets the valid payload length within the owned slice.
    /// </summary>
    public int Length { get; private set; }

    /// <inheritdoc/>
    public bool IsReliable { get; set; }

    /// <inheritdoc/>
    public bool EncryptedOnWire { get; set; }

    /// <summary>
    /// Gets the capacity of the owned slice (from <c>_start</c> to end of the array).
    /// </summary>
    public int Capacity => _buffer is null ? 0 : _buffer.Length - _start;

    /// <summary>
    /// Gets the total capacity (underlying array length).
    /// </summary>
    public int RawCapacity => _buffer?.Length ?? 0;

    /// <summary>
    /// Gets the number of bytes reserved directly in front of the payload for a transport header
    /// (see <see cref="Rent(int, bool, int)"/>). Always 0 for leases created any other way.
    /// </summary>
    public int Headroom => _buffer is null ? 0 : _headroom;

    /// <summary>
    /// Gets the backing array segment that starts <paramref name="headerSize"/> bytes before the
    /// payload and ends at the end of the payload, so a transport can write its length header in
    /// place and send header + payload without copying.
    /// </summary>
    /// <param name="headerSize">The header size to prepend; must not exceed <see cref="Headroom"/>.</param>
    /// <param name="segment">The <c>[header][payload]</c> segment when successful.</param>
    /// <returns><see langword="true"/> when enough headroom was reserved.</returns>
    /// <remarks>
    /// The header bytes belong to this lease's reserved headroom and are never part of
    /// <see cref="Span"/>/<see cref="Memory"/>, so writing them does not change the payload.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSegmentWithHeader(int headerSize, out ArraySegment<byte> segment)
    {
        byte[]? buffer = _buffer;
        if (buffer is null || headerSize <= 0 || headerSize > _headroom)
        {
            segment = default;
            return false;
        }

        segment = new ArraySegment<byte>(buffer, _start - headerSize, this.Length + headerSize);
        return true;
    }

    /// <summary>
    /// Gets or sets whether the slice should be zeroed before returning to the pool.
    /// </summary>
    public bool ZeroOnDispose { get; set; }

    /// <summary>
    /// Writable span over the valid payload slice.
    /// </summary>
    [SuppressMessage("Style", "IDE0301:Simplify collection initialization", Justification = "<Pending>")]
    public Span<byte> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer is null ? Span<byte>.Empty : new Span<byte>(_buffer, _start, this.Length);
    }

    /// <summary>
    /// Writable span over the full owned slice (capacity).
    /// </summary>
    [SuppressMessage("Style", "IDE0301:Simplify collection initialization", Justification = "<Pending>")]
    public Span<byte> SpanFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer is null ? Span<byte>.Empty : new Span<byte>(_buffer, _start, this.Capacity);
    }

    /// <summary>
    /// Read-only view of the valid payload slice.
    /// </summary>
    public ReadOnlyMemory<byte> Memory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(_buffer, _start, this.Length);
    }

    #endregion Properties

    #region APIs

#if DEBUG

    /// <summary>
    /// Convenient ArraySegment over the valid payload slice.
    /// </summary>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArraySegment<byte> AsSegment()
        => _buffer is null ? default : new ArraySegment<byte>(_buffer, _start, this.Length);

#endif

    /// <inheritdoc/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRent() => Volatile.Write(ref _isRented, 1);

    /// <summary>
    /// Increases the reference count so multiple consumers can hold this lease safely.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the lease has already released its underlying buffer.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the internal reference count becomes invalid.</exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Retain()
    {
        /*
         * [Reference Counting]
         * Increment the ref-count atomically. If multiple consumers need 
         * to hold the same buffer (e.g. broadcast), they call Retain().
         * The buffer is only returned to the pool when the last owner calls Dispose().
         */
        ObjectDisposedException.ThrowIf(_buffer is null, nameof(BufferLease));

        int newValue = Interlocked.Increment(ref _refCount);

        if (newValue <= 1)
        {
            THROW_RETAIN_ERROR(newValue);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_RETAIN_ERROR(int newValue)
        => throw new InternalErrorException(
            $"[{nameof(BufferLease)}] Invalid ref-count after Retain: value={newValue}, thread={System.Environment.CurrentManagedThreadId}.");

    /// <summary>
    /// Sets the valid payload length (must be 0..Capacity).
    /// </summary>
    /// <param name="length">The new payload length.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is negative or larger than <see cref="Capacity"/>.</exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitLength(int length)
    {
        if ((uint)length > (uint)this.Capacity)
        {
            THROW_OUT_OF_RANGE();
        }

        this.Length = length;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_OUT_OF_RANGE() => throw new ArgumentOutOfRangeException("length");

    /// <summary>
    /// Releases a reference. When the count reaches zero and not detached, returns the array to <see cref="IBufferPoolManager"/>.
    /// </summary>
    /// <remarks>
    /// Double-dispose is tolerated and becomes a debug-only diagnostic instead of throwing.
    /// </remarks>
    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        int newValue = Interlocked.Decrement(ref _refCount);

        if (newValue > 0)
        {
            return;
        }

        if (newValue < 0)
        {
#if DEBUG
            Debug.Fail($"BufferLease double dispose detected. RefCount went to {newValue}");
#endif
            return;
        }

        GC.SuppressFinalize(this);

        if (Volatile.Read(ref _detached) == 1)
        {
            _buffer = null;
            _start = 0;
            this.Length = 0;
            return;
        }

        byte[]? buf = Interlocked.Exchange(ref _buffer, null);
        int start = _start;
        int len = this.Length;

        if (buf is not null)
        {
            if (len > 0)
            {
                Span<byte> slice = new(buf, start, len);

                if (this.ZeroOnDispose)
                {
                    slice.Clear();
                }
#if DEBUG
                if (EnablePoisonOnDispose)
                {
                    slice.Fill(PoisonByte);
                }
#endif
            }

            ByteArrayPool.Return(buf);
        }

        // Return the BufferLease shell to the free-list for reuse.
        if (IsPoolingEnabled)
        {
            // Fast path: delegate to configured ObjectPoolManager
            if (Interlocked.Exchange(ref _isRented, 0) == 1)
            {
                IObjectPoolManager? mgr = Volatile.Read(ref ShellPoolManager);

                if (mgr is not null)
                {
                    mgr.Return(this);
                    return;
                }
            }

            this.ResetForPool();

            // 1. Try push to Thread-Local Cache
            ThreadLocalLeaseCache cache = t_localCache ??= new ThreadLocalLeaseCache(s_threadLocalMaxSlots);
            if (cache.Count < cache.MaxSlots)
            {
                cache.Slots[cache.Count++] = this;
                return;
            }

            // 2. Try push to Shared Pool with striped index
            int threadId = System.Environment.CurrentManagedThreadId;
            int mask = s_sharedPoolMask;
            int startIdx = threadId & mask;

            for (int i = 0; i < s_sharedPoolSize; i++)
            {
                int idx = (startIdx + i) & mask;
                if (s_sharedPool[idx] is null && Interlocked.CompareExchange(ref s_sharedPool[idx], this, null) is null)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Transfers ownership of the underlying array slice to the caller (no pool return on Dispose).
    /// After a successful release, this instance becomes empty and disposing it is a no-op.
    /// Only allowed when this is the last reference (refCount == 1).
    /// </summary>
    /// <param name="buffer">The detached buffer.</param>
    /// <param name="start">The starting offset of the detached slice.</param>
    /// <param name="length">The length of the detached slice.</param>
    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool ReleaseOwnership(out byte[]? buffer, out int start, out int length)
    {
        /*
         * [Ownership Transfer]
         * This allows a consumer to 'take' the raw byte[] out of the lease.
         * The lease marks itself as 'detached' and will no longer return the 
         * buffer to the pool when disposed.
         */
        // Ensure single-owner detach (avoid breaking other holders)
        if (Volatile.Read(ref _refCount) != 1)
        {
            buffer = null; start = 0; length = 0;
            return false;
        }

        if (Interlocked.Exchange(ref _detached, 1) == 1)
        {
            buffer = null; start = 0; length = 0;
            return false; // Already detached
        }

        buffer = Interlocked.Exchange(ref _buffer, null);
        start = _start;
        length = this.Length;

        _start = 0;
        _headroom = 0;
        this.Length = 0;
        return buffer is not null;
    }

    /// <summary>
    /// Auto-rents a buffer from <see cref="IBufferPoolManager"/> and returns a new empty slice [start=0, length=0].
    /// Caller writes to <see cref="SpanFull"/> then calls <see cref="CommitLength(int)"/>.
    /// </summary>
    /// <param name="capacity">The minimum capacity to rent.</param>
    /// <param name="zeroOnDispose">Whether to clear the buffer before returning it to the pool.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is negative.</exception>
    /// <exception cref="OutOfMemoryException">Thrown when no backing array can be rented.</exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BufferLease Rent(
        int capacity,
        bool zeroOnDispose = false)
    {
        byte[] arr = ByteArrayPool.Rent(capacity);
        BufferLease lease = RENT_LEASE_SHELL();
        lease.Initialize(arr, start: 0, length: 0, zeroOnDispose);
        return lease;
    }

    /// <summary>
    /// Rents a buffer whose payload starts <paramref name="headroom"/> bytes into the array, keeping
    /// that prefix free for a transport header (see <see cref="TryGetSegmentWithHeader"/>).
    /// The lease behaves exactly like one from <see cref="Rent(int, bool)"/>: <see cref="Span"/>,
    /// <see cref="SpanFull"/> and <see cref="Capacity"/> all start after the headroom.
    /// </summary>
    /// <param name="capacity">The minimum payload capacity.</param>
    /// <param name="zeroOnDispose">Whether to clear the used payload before returning it to the pool.</param>
    /// <param name="headroom">Bytes to reserve in front of the payload.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> or <paramref name="headroom"/> is negative.</exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BufferLease Rent(int capacity, bool zeroOnDispose, int headroom)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(headroom);

        byte[] arr = ByteArrayPool.Rent(capacity + headroom);
        BufferLease lease = RENT_LEASE_SHELL();
        lease.Initialize(arr, start: headroom, length: 0, zeroOnDispose);
        lease._headroom = headroom;
        return lease;
    }

    /// <summary>
    /// Creates a <see cref="BufferLease"/> by copying the source into a newly rented buffer.
    /// </summary>
    /// <param name="src">The source data to copy.</param>
    /// <param name="zeroOnDispose">Whether to clear the buffer before returning it to the pool.</param>
    /// <exception cref="OutOfMemoryException">Thrown when no backing array can be rented for the copied data.</exception>
    public static BufferLease CopyFrom(ReadOnlySpan<byte> src, bool zeroOnDispose = false)
    {
        byte[] arr = ByteArrayPool.Rent(src.Length);
        src.CopyTo(arr);
        BufferLease lease = RENT_LEASE_SHELL();
        lease.Initialize(arr, start: 0, length: src.Length, zeroOnDispose);
        return lease;
    }

    /// <summary>
    /// Wraps an array that was previously rented from <see cref="IBufferPoolManager"/> (payload starts at 0).
    /// Caller asserts the array comes from the same pool and is safe to own here.
    /// </summary>
    /// <param name="buffer">The rented buffer to wrap.</param>
    /// <param name="length">The payload length within the buffer.</param>
    /// <param name="zeroOnDispose">Whether to clear the buffer before returning it to the pool.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is outside the bounds of <paramref name="buffer"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BufferLease FromRented(
        byte[] buffer,
        int length,
        bool zeroOnDispose = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        BufferLease lease = RENT_LEASE_SHELL();
        lease.Initialize(buffer, start: 0, length: length, zeroOnDispose);
        return lease;
    }

    /// <summary>
    /// Wraps a slice [<paramref name="start"/>..&lt;start+length&gt;) of a previously rented array from <see cref="IBufferPoolManager"/>.
    /// This is the key API for zero-copy handoff of a payload located after a protocol header.
    /// </summary>
    /// <param name="buffer">The rented buffer to wrap.</param>
    /// <param name="start">The start offset of the slice.</param>
    /// <param name="length">The length of the slice.</param>
    /// <param name="zeroOnDispose">Whether to clear the buffer before returning it to the pool.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the requested slice is outside the bounds of <paramref name="buffer"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BufferLease TakeOwnership(
        byte[] buffer,
        int start,
        int length,
        bool zeroOnDispose = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        BufferLease lease = RENT_LEASE_SHELL();
        lease.Initialize(buffer, start, length, zeroOnDispose);
        return lease;
    }

    #endregion APIs
}
