// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Environment.Memory;

namespace Nalix.Network.Internal.Transport;

internal sealed partial class SocketConnection
{
    /// <summary>
    /// Upper bound, in bytes, of the frames coalesced into one socket write by <see cref="SendBatchAsync"/>.
    /// </summary>
    internal const int MaxBatchBytes = 64 * 1024;

    /// <summary>
    /// Gets whether every frame in <paramref name="frames"/> can go out as a single un-fragmented
    /// UInt16-prefixed frame, which is what <see cref="SendBatchAsync"/> writes.
    /// </summary>
    public bool CanSendBatch(ReadOnlySpan<IBufferLease> frames)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            if (!this.CanSendWithReservedHeader(frames[i].Length))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Sends <paramref name="frames"/> in order, each as its own length-prefixed frame, but copied into a
    /// shared buffer so the batch costs one socket write per <see cref="MaxBatchBytes"/> instead of one per frame.
    /// </summary>
    /// <param name="frames">The frame payloads; <see cref="CanSendBatch"/> must hold for them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SendResult> SendBatchAsync(ReadOnlyMemory<IBufferLease> frames, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(SocketConnection));

        int index = 0;
        while (index < frames.Length)
        {
            int first = index;
            int totalLength = 0;

            while (index < frames.Length)
            {
                int frameLength = frames.Span[index].Length + HeaderSize;
                if (totalLength > 0 && totalLength + frameLength > MaxBatchBytes)
                {
                    break;
                }

                totalLength += frameLength;
                index++;
            }

            byte[] buffer = BufferLease.ByteArrayPool.Rent(totalLength);
            int written = 0;
            for (int i = first; i < index; i++)
            {
                ReadOnlySpan<byte> payload = frames.Span[i].Span;
                BinaryPrimitives.WriteUInt16LittleEndian(MemoryExtensions.AsSpan(buffer, written), (ushort)(payload.Length + HeaderSize));
                payload.CopyTo(MemoryExtensions.AsSpan(buffer, written + HeaderSize));
                written += payload.Length + HeaderSize;
            }

            // SEND_BUFFER_ASYNC returns the buffer to the pool and raises the post callback once.
            SendResult result = await this.SEND_BUFFER_ASYNC(buffer, 0, totalLength, pooled: true, cancellationToken).ConfigureAwait(false);
            if (result != SendResult.Success)
            {
                return result;
            }

            for (int i = first + 1; i < index; i++)
            {
                this.INVOKE_POST_CALLBACK();
            }
        }

        return SendResult.Success;
    }
}
