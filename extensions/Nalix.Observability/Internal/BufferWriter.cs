// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using Nalix.Environment.Memory;

namespace Nalix.Observability.Internal;

/// <summary>
/// A lightweight class-allocated buffer writer that rents from <see cref="BufferLease.ByteArrayPool"/>
/// and allows extracting the written data directly into a <see cref="BufferLease"/> without allocation.
/// </summary>
internal sealed class BufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferWriter"/> class with the specified initial capacity.
    /// </summary>
    /// <param name="initialCapacity">The initial capacity of the rented buffer.</param>
    public BufferWriter(int initialCapacity) => _buffer = BufferLease.ByteArrayPool.Rent(initialCapacity <= 0 ? 256 : initialCapacity);

    /// <summary>
    /// Gets the bytes written so far.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

    /// <summary>
    /// Discards the written bytes while keeping the rented buffer for reuse.
    /// </summary>
    public void Clear() => _index = 0;

    /// <summary>
    /// Advances the current index by the specified count.
    /// </summary>
    /// <param name="count">The number of bytes written.</param>
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ObjectDisposedException.ThrowIf(_buffer is null, this);

        if (_index + count > _buffer.Length)
        {
            throw new InvalidOperationException("Cannot advance past the buffer capacity.");
        }

        _index += count;
    }

    /// <summary>
    /// Gets a <see cref="Memory{T}"/> block to write to.
    /// </summary>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        this.EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_index);
    }

    /// <summary>
    /// Gets a <see cref="Span{T}"/> block to write to.
    /// </summary>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        this.EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_index);
    }

    /// <summary>
    /// Extracts the written data into a <see cref="BufferLease"/>, transferring ownership of the rented buffer.
    /// </summary>
    public BufferLease ExtractLease()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);

        byte[] buf = _buffer;
        int length = _index;

        _buffer = null;
        _index = 0;

        return BufferLease.TakeOwnership(buf, start: 0, length: length);
    }

    /// <summary>
    /// Disposes the buffer writer and returns the rented buffer to the pool if it hasn't been extracted.
    /// </summary>
    public void Dispose()
    {
        if (_buffer is not null)
        {
            BufferLease.ByteArrayPool.Return(_buffer);
            _buffer = null;
        }
        _index = 0;
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        ObjectDisposedException.ThrowIf(_buffer is null, this);

        int required = _index + (sizeHint == 0 ? 1 : sizeHint);
        if (required > _buffer.Length)
        {
            int newSize = Math.Max(_buffer.Length * 2, required);
            byte[] newBuffer = BufferLease.ByteArrayPool.Rent(newSize);
            _buffer.AsSpan(0, _index).CopyTo(newBuffer);
            BufferLease.ByteArrayPool.Return(_buffer);
            _buffer = newBuffer;
        }
    }
}
