// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nalix.Environment.Configuration;
using Nalix.Environment.Internal;
using Nalix.Environment.Options;

namespace Nalix.Environment.Memory;

/// <summary>
/// A mutable, growable write buffer for high-performance serialization.
/// Internally stores a <see cref="Span{Byte}"/> view and, when rented,
/// keeps the backing <see cref="byte"/> array to allow expansion and pooling.
/// </summary>
[DebuggerNonUserCode]
[SkipLocalsInit]
[ExcludeFromCodeCoverage]
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Sequential)]
[DebuggerDisplay("Len={Length}, Written={WrittenCount}, Rent={_rent}, OWNER={( _owner != null )}")]
public ref struct DataWriter
{
    #region Fields

    /// <summary>
    /// Current writable view
    /// </summary>
    private Span<byte> _span;

    /// <summary>
    /// Backing array when owned (rented or external array); null if wrapping an external span
    /// </summary>
    private byte[]? _owner;

    /// <summary>
    /// True when the backing array was rented from ArrayPool and must be returned on Dispose
    /// </summary>
    private readonly bool _rent;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Creates a writer that rents its internal buffer from the shared array pool.
    /// </summary>
    /// <param name="size">Initial capacity in bytes (must be &gt; 0).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="size"/> is not positive.</exception>
    public DataWriter(int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "SIZE must be greater than zero.");
        }

        _rent = true;
        _owner = BufferLease.ByteArrayPool.Rent(size);
        Debug.Assert(_owner.Length >= size, "ArrayPool returned insufficient buffer");
        _span = MemoryExtensions.AsSpan(_owner);

        this.WrittenCount = 0;
    }

    /// <summary>
    /// Creates a writer over an existing external array (no renting, no automatic return).
    /// </summary>
    /// <param name="buffer">External backing array (length must be &gt; 0).</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="buffer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="buffer"/> has zero length.</exception>
    public DataWriter(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer, nameof(buffer));

        if (buffer.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(buffer), "SIZE must be greater than zero.");
        }

        _rent = false;
        _owner = buffer;                 // owns an external array (but not rented) -> cannot expand by rent policy
        _span = MemoryExtensions.AsSpan(buffer);

        this.WrittenCount = 0;
    }

    /// <summary>
    /// Creates a writer over an existing external array (no renting, no automatic return).
    /// </summary>
    /// <param name="span">External backing array (length must be &gt; 0).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="span"/> has zero length.</exception>
    public DataWriter(Span<byte> span)
    {
        if (span.Length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(span), "SIZE must be greater than zero.");
        }

        _span = span;       // direct span view (stackalloc, sliced array, etc.)
        _owner = null;      // no backing array ownership
        _rent = false;      // not rented -> cannot Expand()

        this.WrittenCount = 0;
    }

    #endregion Constructors

    #region Properties

    /// <summary>
    /// Gets the number of bytes committed via <see cref="Advance(int)"/>.
    /// </summary>
    [Pure]
    public int WrittenCount { get; private set; }

    /// <summary>
    /// Gets a span representing the remaining unwritten segment of the buffer.
    /// </summary>
    [Pure]
    public readonly Span<byte> FreeBuffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _span[this.WrittenCount..];
    }

    #endregion Properties

    #region APIs

    /// <summary>
    /// Advances the write cursor by the specified count.
    /// </summary>
    /// <param name="count">Number of bytes to commit (must fit into <see cref="FreeBuffer"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if out of bounds or non-positive.</exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        if (count <= 0 || (uint)(this.WrittenCount + count) > (uint)_span.Length)
        {
            Throw.AdvanceOutOfBound();
        }

        this.WrittenCount += count;
    }

    /// <summary>
    /// Returns a reference to the first byte of <see cref="FreeBuffer"/> for ref-based writes.
    /// Call <see cref="Expand(int)"/> beforehand to ensure capacity.
    /// </summary>
    [DebuggerStepThrough]
    [UnscopedRef]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref byte GetFreeBufferReference() => ref MemoryMarshal.GetReference(this.FreeBuffer);

    /// <summary>
    /// Ensures at least <paramref name="minimumSize"/> bytes are available in <see cref="FreeBuffer"/>.
    /// Expands only when this instance owns a rented array; throws for fixed/external buffers.
    /// </summary>
    /// <param name="minimumSize">Required free space in bytes (must be &gt; 0).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if non-positive.</exception>
    /// <exception cref="InvalidOperationException">Thrown when expansion is not allowed.</exception>
    /// <exception cref="OutOfMemoryException">Thrown when a larger backing buffer cannot be rented.</exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public void Expand(int minimumSize)
    {
        if (minimumSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                $"Invalid minimumSize: value={minimumSize}, required>0.");
        }

        if (_span.Length - this.WrittenCount >= minimumSize)
        {
            return;
        }

        if (!_rent) // external array (or external span if we ever add such ctor) cannot expand by policy
        {
            Throw.FixedBufferExpansion();
        }

        // Use checked arithmetic to prevent integer overflow attacks
        // and enforce a hard limit to prevent OOM-based DoS.
        // The limit is read from global configuration to allow per-deployment tuning.
        int limit = ConfigurationManager.Instance.Get<MemoryOptions>().MaxWriterCapacity;

        int current = _owner?.Length ?? 0;
        int needed = this.WrittenCount + minimumSize;
        int newSize;

        try
        {
            checked
            {
                newSize = current <= 0 ? needed : Math.Max(current * 2, needed);
            }

            if (newSize > limit)
            {
                throw new Abstractions.Exceptions.SerializationFailureException(
                    $"DataWriter expansion failed: requested capacity {newSize} bytes exceeds the allowed limit of {limit} bytes. (Config: Memory.MaxWriterCapacity)");
            }
        }
        catch (OverflowException)
        {
            throw new Abstractions.Exceptions.SerializationFailureException(
                $"DataWriter expansion failed: integer overflow when calculating new size for {needed} bytes.");
        }

        byte[] newOwner = BufferLease.ByteArrayPool.Rent(newSize);
        if (this.WrittenCount > 0)
        {
            if (current <= 128)
            {
                _span[..this.WrittenCount].CopyTo(newOwner);
            }
            else
            {
                CopyBytes(_owner, newOwner, this.WrittenCount);
            }
        }

        byte[]? oldOwner = _owner;

        _owner = newOwner;
        _span = MemoryExtensions.AsSpan(_owner);

        if (oldOwner is not null)
        {
            // Scrub only the bytes this writer produced (serialized packet data); the pool no
            // longer clears whole arrays on return.
            MemoryExtensions.AsSpan(oldOwner, 0, Math.Min(this.WrittenCount, oldOwner.Length)).Clear();
            BufferLease.ByteArrayPool.Return(oldOwner);
        }

        [StackTraceHidden]
        [DebuggerStepThrough]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyBytes(byte[]? src, byte[] dst, int count)
        {
            ArgumentNullException.ThrowIfNull(src);
            ArgumentNullException.ThrowIfNull(dst);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if ((uint)count > (uint)src.Length || (uint)count > (uint)dst.Length)
            {
                THROW_OUT_OF_RANGE();
            }

            fixed (byte* pSrc = &MemoryMarshal.GetArrayDataReference(src))
            {
                fixed (byte* pDst = &MemoryMarshal.GetArrayDataReference(dst))
                {
                    Buffer.MemoryCopy(pSrc, pDst, (nuint)count, (nuint)count);
                }
            }
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_OUT_OF_RANGE() => throw new ArgumentOutOfRangeException("count");

    /// <summary>
    /// Writes any unmanaged value directly to the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        int needed = this.WrittenCount + size;
        if ((uint)needed > (uint)_span.Length)
        {
            this.Expand(size);
        }

        ref byte ptr = ref Unsafe.Add(ref MemoryMarshal.GetReference(_span), this.WrittenCount);
        Unsafe.WriteUnaligned(ref ptr, value);
        this.WrittenCount = needed;
    }

    /// <summary>
    /// Writes a span of bytes directly to the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        int needed = this.WrittenCount + value.Length;
        if ((uint)needed > (uint)_span.Length)
        {
            this.Expand(value.Length);
        }

        value.CopyTo(_span[this.WrittenCount..]);
        this.WrittenCount = needed;
    }

    /// <summary>
    /// Writes a byte array directly to the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(byte[] value)
    {
        if (value is null || value.Length == 0)
        {
            return;
        }

        int needed = this.WrittenCount + value.Length;
        if ((uint)needed > (uint)_span.Length)
        {
            this.Expand(value.Length);
        }

        value.AsSpan().CopyTo(_span[this.WrittenCount..]);
        this.WrittenCount = needed;
    }

    /// <summary>
    /// Copies the committed data into a new tightly sized array.
    /// </summary>
    [Pure]
    [DebuggerStepThrough]
    public readonly byte[] ToArray()
    {
        int n = this.WrittenCount;
        byte[] result = new byte[n];
        if (n > 0)
        {
            _span[..n].CopyTo(result);
        }

        return result;
    }

    /// <summary>
    /// Clears state and returns the rented array to the pool when applicable.
    /// </summary>
    [Pure]
    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (_owner is not null)
        {
            if (_rent)
            {
                // Scrub only the bytes this writer produced; the pool no longer clears on return.
                MemoryExtensions.AsSpan(_owner, 0, Math.Min(this.WrittenCount, _owner.Length)).Clear();
                BufferLease.ByteArrayPool.Return(_owner);
            }

            _owner = null;
        }

        _span = [];
        this.WrittenCount = 0;
    }

    #endregion APIs
}
