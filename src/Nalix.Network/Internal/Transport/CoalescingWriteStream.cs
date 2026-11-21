// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Environment.Memory;

namespace Nalix.Network.Internal.Transport;

/// <summary>
/// Pass-through stream placed between a <see cref="System.Net.WebSockets.WebSocket"/> and its socket stream
/// so a batch of WebSocket messages can leave in one socket write.
/// </summary>
/// <remarks>
/// While corked, writes are appended to a pooled buffer instead of reaching the inner stream. The buffer is
/// written out by the first write that arrives after <see cref="Uncork"/> (or once it reaches
/// <see cref="MaxBufferedBytes"/>), together with that write's own bytes, so message order is kept.
/// The gate only matters for <see cref="FlushPendingAsync"/>, which runs outside the WebSocket's own write serialization.
/// </remarks>
internal sealed class CoalescingWriteStream(Stream inner) : Stream
{
    /// <summary>Buffered bytes at which a corked stream writes through anyway.</summary>
    internal const int MaxBufferedBytes = 64 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _buffer;
    private int _buffered;
    private volatile bool _corked;

    /// <summary>Starts holding writes back. The caller must send one more message after <see cref="Uncork"/>.</summary>
    public void Cork() => _corked = true;

    /// <summary>Stops holding writes back; the next write carries everything buffered so far.</summary>
    public void Uncork() => _corked = false;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanTimeout => inner.CanTimeout;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
    public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _gate.Wait();
        try
        {
            if (_buffered == 0 && !_corked)
            {
                inner.Write(buffer, offset, count);
                return;
            }

            this.APPEND(new ReadOnlySpan<byte>(buffer, offset, count));
            if (!_corked || _buffered >= MaxBufferedBytes)
            {
                byte[] pending = this.TAKE(out int length);
                try
                {
                    inner.Write(pending, 0, length);
                }
                finally
                {
                    BufferLease.ByteArrayPool.Return(pending);
                }
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buffered == 0 && !_corked)
            {
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }

            this.APPEND(buffer.Span);
            if (!_corked || _buffered >= MaxBufferedBytes)
            {
                await this.WRITE_PENDING_ASYNC(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Writes out whatever is still buffered. Only needed when a corked batch ended without the
    /// final write that normally carries the buffer.
    /// </summary>
    public async ValueTask FlushPendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buffered > 0)
            {
                await this.WRITE_PENDING_ASYNC(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                BufferLease.ByteArrayPool.Return(buffer);
            }

            inner.Dispose();
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }

    private void APPEND(ReadOnlySpan<byte> data)
    {
        int required = _buffered + data.Length;
        byte[]? buffer = _buffer;

        if (buffer is null || buffer.Length < required)
        {
            byte[] grown = BufferLease.ByteArrayPool.Rent(required);
            if (buffer is not null)
            {
                buffer.AsSpan(0, _buffered).CopyTo(grown);
                BufferLease.ByteArrayPool.Return(buffer);
            }

            _buffer = buffer = grown;
        }

        data.CopyTo(buffer.AsSpan(_buffered));
        _buffered = required;
    }

    private async ValueTask WRITE_PENDING_ASYNC(CancellationToken cancellationToken)
    {
        byte[] pending = this.TAKE(out int length);
        try
        {
            await inner.WriteAsync(new ReadOnlyMemory<byte>(pending, 0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            BufferLease.ByteArrayPool.Return(pending);
        }
    }

    private byte[] TAKE(out int length)
    {
        byte[] pending = _buffer!;
        length = _buffered;
        _buffer = null;
        _buffered = 0;
        return pending;
    }
}
