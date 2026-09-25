// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;

namespace Nalix.Network.Internal.Transport;

internal sealed partial class SocketConnection
{
    /// <summary>
    /// Sends data synchronously.
    /// Small packets (≤ <see cref="PacketConstants.StackAllocLimit"/>) are framed on the
    /// stack; larger ones use a pooled heap buffer.
    /// </summary>
    /// <param name="data"></param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public SendResult Send(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(SocketConnection));

        if (data.IsEmpty)
        {
            throw new ArgumentException("Data must not be empty.", nameof(data));
        }

        if (_framing == TransportFraming.VarIntLengthPrefixed)
        {
            return this.SEND_VARINT(data);
        }

        if (data.Length >= s_fragmentOptions.MaxChunkSize)
        {
            return this.SEND_FRAGMENTED(data);
        }

        long totalLengthLong = (long)data.Length + HeaderSize;

        if (totalLengthLong > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                totalLengthLong,
                $"Non-fragmented frame size must not exceed {ushort.MaxValue} bytes.");
        }

        int totalLength = (int)totalLengthLong;

        return this.SEND_SAFE(data, totalLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SendResult SEND_SAFE(ReadOnlySpan<byte> data, int totalLength)
    {
        /*
         * [Fast Path: Stack Allocation]
         * For small packets (determined by StackAllocLimit), we format the frame
         * directly on the stack. This is zero-allocation and extremely fast.
         * The frame consists of: [2 bytes Length] + [Payload].
         */
        if (data.Length <= PacketConstants.StackAllocLimit)
        {
            Span<byte> frameS = stackalloc byte[totalLength];
            WRITE_FRAME_HEADER(frameS, (ushort)totalLength, data);

            int sent = 0;
            SendResult result = SendResult.Success;
            lock (_sendLock)
            {
                try
                {
                    while (sent < frameS.Length)
                    {
                        int n = _socket.Send(frameS[sent..]);
                        if (n == 0)
                        {
                            result = SendResult.PeerClosed;
                            break;
                        }

                        sent += n;
                        _ = Interlocked.Add(ref _bytesSent, n);
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode is
                       SocketError.ConnectionReset or
                       SocketError.ConnectionAborted or
                       SocketError.Shutdown or
                       SocketError.OperationAborted)
                {
                    result = SendResult.Aborted;
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    result = SendResult.Failed;
                }
            }

            if (result != SendResult.Success)
            {
                this.CANCEL_RECEIVE_ONCE();
                this.INVOKE_CLOSE_ONCE();
                return result;
            }

            _connectionOwner?.OnFrameSent();
            return SendResult.Success;
        }

        /*
         * [Slow Path: Pooled Heap Buffer]
         * For larger packets that don't fit on the stack, we rent a buffer from
         * the ArrayPool. This prevents GC pressure while still supporting large
         * payloads.
         */
        byte[] heapBuf = BufferLease.ByteArrayPool.Rent(totalLength);
        try
        {
            BinaryPrimitives.WriteUInt16LittleEndian(MemoryExtensions.AsSpan(heapBuf), (ushort)totalLength);
            data.CopyTo(MemoryExtensions.AsSpan(heapBuf, HeaderSize));

            int sent = 0;
            SendResult result = SendResult.Success;
            lock (_sendLock)
            {
                try
                {
                    while (sent < totalLength)
                    {
                        int n = _socket.Send(heapBuf, sent, totalLength - sent, SocketFlags.None);
                        if (n == 0)
                        {
                            result = SendResult.PeerClosed;
                            break;
                        }

                        sent += n;
                        _ = Interlocked.Add(ref _bytesSent, n);
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode is
                       SocketError.ConnectionReset or
                       SocketError.ConnectionAborted or
                       SocketError.Shutdown or
                       SocketError.OperationAborted)
                {
                    result = SendResult.Aborted;
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    result = SendResult.Failed;
                }
            }

            if (result != SendResult.Success)
            {
                this.CANCEL_RECEIVE_ONCE();
                this.INVOKE_CLOSE_ONCE();
                return result;
            }

            this.INVOKE_POST_CALLBACK();
            return SendResult.Success;
        }
        finally
        {
            BufferLease.ByteArrayPool.Return(heapBuf);
        }
    }

    /// <summary>
    /// Sends data asynchronously. Uses a pooled heap buffer for framing.
    /// </summary>
    /// <param name="data"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see langword="true"/> if the data was sent successfully.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<SendResult> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(SocketConnection));

        if (data.IsEmpty)
        {
            return ValueTask.FromException<SendResult>(new ArgumentException("Data must not be empty.", nameof(data)));
        }

        if (_framing == TransportFraming.VarIntLengthPrefixed)
        {
            return this.SEND_VARINT_ASYNC(data, cancellationToken);
        }

        if (data.Length >= s_fragmentOptions.MaxChunkSize)
        {
            return this.SEND_FRAGMENTED_ASYNC(data, cancellationToken);
        }

        long totalLengthLong = (long)data.Length + HeaderSize;
        if (totalLengthLong > ushort.MaxValue)
        {
            return ValueTask.FromException<SendResult>(new ArgumentOutOfRangeException(
                nameof(data),
                totalLengthLong,
                $"Non-fragmented frame size must not exceed {ushort.MaxValue} bytes."));
        }

        int totalLength = (int)totalLengthLong;

        return this.SEND_ASYNC_SAFE(data, totalLength, cancellationToken);
    }

    /// <summary>
    /// Sends a frame whose 2-byte length header space was reserved by the caller directly in front
    /// of the payload, so no rent + copy is needed to prepend the header.
    /// </summary>
    /// <param name="buffer">The array holding <c>[header space][payload]</c>. Owned by the caller; not returned to any pool.</param>
    /// <param name="offset">Offset of the reserved header space (the payload starts at <c>offset + 2</c>).</param>
    /// <param name="payloadLength">Payload length in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The send result. The caller must keep <paramref name="buffer"/> alive until it completes.</returns>
    /// <remarks>
    /// Only valid for <see cref="TransportFraming.UInt16LengthPrefixed"/> framing and frames below the
    /// fragmentation threshold; <see cref="CanSendWithReservedHeader"/> checks both.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<SendResult> SendWithReservedHeaderAsync(byte[] buffer, int offset, int payloadLength, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(SocketConnection));

        int totalLength = payloadLength + HeaderSize;
        BinaryPrimitives.WriteUInt16LittleEndian(MemoryExtensions.AsSpan(buffer, offset, HeaderSize), (ushort)totalLength);

        return this.SEND_BUFFER_ASYNC(buffer, offset, totalLength, pooled: false, cancellationToken);
    }

    /// <summary>
    /// Gets whether a payload of <paramref name="payloadLength"/> bytes can be sent through
    /// <see cref="SendWithReservedHeaderAsync"/> (single un-fragmented UInt16-prefixed frame).
    /// </summary>
    /// <param name="payloadLength">Payload length in bytes.</param>
    /// <returns><see langword="true"/> when the reserved-header fast path applies.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanSendWithReservedHeader(int payloadLength)
        => _framing != TransportFraming.VarIntLengthPrefixed
           && payloadLength > 0
           && payloadLength < s_fragmentOptions.MaxChunkSize
           && payloadLength + HeaderSize <= ushort.MaxValue;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<SendResult> SEND_ASYNC_SAFE(ReadOnlyMemory<byte> data, int totalLength, CancellationToken cancellationToken)
    {
        byte[] heapBuf = BufferLease.ByteArrayPool.Rent(totalLength);

        try
        {
            WRITE_FRAME_HEADER(MemoryExtensions.AsSpan(heapBuf), (ushort)totalLength, data.Span);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            BufferLease.ByteArrayPool.Return(heapBuf);
            return ValueTask.FromResult(SendResult.Failed);
        }

        return this.SEND_BUFFER_ASYNC(heapBuf, 0, totalLength, pooled: true, cancellationToken);
    }

    /// <summary>
    /// Writes <c>buffer[offset..offset+totalLength)</c> (a complete frame) to the socket.
    /// When <paramref name="pooled"/> is <see langword="true"/> the buffer is returned to the pool once the write ends.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ValueTask<SendResult> SEND_BUFFER_ASYNC(byte[] heapBuf, int offset, int totalLength, bool pooled, CancellationToken cancellationToken)
    {
        try
        {
            int sent = 0;
            while (sent < totalLength)
            {
                ValueTask<int> vt = _socket.SendAsync(MemoryExtensions
                                           .AsMemory(heapBuf, offset + sent, totalLength - sent), SocketFlags.None, cancellationToken);

                if (vt.IsCompletedSuccessfully)
                {
                    int n = vt.Result;
                    if (n == 0)
                    {
                        RETURN_IF_POOLED(heapBuf, pooled);
                        this.CANCEL_RECEIVE_ONCE();
                        this.INVOKE_CLOSE_ONCE();
                        return ValueTask.FromResult(SendResult.PeerClosed);
                    }

                    sent += n;
                    _ = Interlocked.Add(ref _bytesSent, n);
                }
                else
                {
                    return AWAIT_SEND(this, vt, heapBuf, offset, sent, totalLength, pooled, cancellationToken);
                }
            }

            this.INVOKE_POST_CALLBACK();
            RETURN_IF_POOLED(heapBuf, pooled);
            return ValueTask.FromResult(SendResult.Success);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is
               SocketError.ConnectionReset or
               SocketError.ConnectionAborted or
               SocketError.Shutdown or
               SocketError.OperationAborted)
        {
            RETURN_IF_POOLED(heapBuf, pooled);
            return ValueTask.FromResult(SendResult.Aborted);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            RETURN_IF_POOLED(heapBuf, pooled);
            return ValueTask.FromResult(SendResult.Failed);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        static async ValueTask<SendResult> AWAIT_SEND(SocketConnection self, ValueTask<int> vt, byte[] heapBuf, int offset, int sent, int totalLength, bool pooled, CancellationToken token)
        {
            try
            {
                int n = await vt.ConfigureAwait(false);
                if (n == 0)
                {
                    self.CANCEL_RECEIVE_ONCE();
                    self.INVOKE_CLOSE_ONCE();
                    return SendResult.PeerClosed;
                }

                sent += n;
                _ = Interlocked.Add(ref self._bytesSent, n);

                while (sent < totalLength)
                {
                    n = await self._socket.SendAsync(MemoryExtensions.AsMemory(heapBuf, offset + sent, totalLength - sent), SocketFlags.None, token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        self.CANCEL_RECEIVE_ONCE();
                        self.INVOKE_CLOSE_ONCE();
                        return SendResult.PeerClosed;
                    }

                    sent += n;
                    _ = Interlocked.Add(ref self._bytesSent, n);
                }

                self.INVOKE_POST_CALLBACK();
                return SendResult.Success;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is
                   SocketError.ConnectionReset or
                   SocketError.ConnectionAborted or
                   SocketError.Shutdown or
                   SocketError.OperationAborted)
            {
                return SendResult.Aborted;
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                return SendResult.Failed;
            }
            finally
            {
                RETURN_IF_POOLED(heapBuf, pooled);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RETURN_IF_POOLED(byte[] buffer, bool pooled)
    {
        if (pooled)
        {
            BufferLease.ByteArrayPool.Return(buffer);
        }
    }

    #region Helper Methods

    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WRITE_FRAME_HEADER(Span<byte> buffer, ushort totalLength, ReadOnlySpan<byte> payload)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, totalLength);
        payload.CopyTo(buffer[HeaderSize..]);
    }

    #endregion Helper Methods

    #region Fragmented Send Helpers

    /// <summary>
    /// Sends large payloads by fragmenting them automatically.
    /// The caller does not need to split the payload manually; this method
    /// turns it into the wire format expected by the receiver and sends each
    /// fragment in order.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2014:Do not use stackalloc in loops", Justification = "<Pending>")]
    private SendResult SEND_FRAGMENTED(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > s_fragmentOptions.MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"Payload exceeds maximum allowed size {s_fragmentOptions.MaxPayloadSize}");
        }

        ushort streamId = FragmentStreamId.Next();
        int chunkBodySize = s_fragmentOptions.MaxChunkSize;
        int totalChunks = (payload.Length + chunkBodySize - 1) / chunkBodySize;

        if (totalChunks > ushort.MaxValue)
        {
            throw new InternalErrorException(
                $"Fragmented payload requires {totalChunks} chunks, which exceeds the {ushort.MaxValue}-chunk wire header limit.");
        }

        Span<byte> headerBuffer = stackalloc byte[FragmentHeader.WireSize];

        /*
         * [Fragmentation Logic]
         * When a payload exceeds MaxChunkSize, we split it into multiple
         * fragments. Each fragment is wrapped in a FragmentHeader and sent
         * as a separate wire frame. The receiver will reassemble these chunks.
         */
        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * chunkBodySize;
            int remaining = payload.Length - offset;
            int thisChunkSize = Math.Min(remaining, chunkBodySize);

            bool isLast = i == totalChunks - 1;

            FragmentHeader fragHeader = new(
                streamId: streamId,
                chunkIndex: (ushort)i,
                totalChunks: (ushort)totalChunks,
                isLast: isLast);

            fragHeader.WriteTo(headerBuffer);

            // Compute the full wire size for this fragment, including the outer
            // length prefix and the fragment header.
            int framePayloadSize = FragmentHeader.WireSize + thisChunkSize;

            int totalFrameSize = HeaderSize + framePayloadSize;

            if (totalFrameSize > ushort.MaxValue)
            {
                throw new InternalErrorException(
                    $"Fragmented frame size {totalFrameSize} exceeds the {ushort.MaxValue}-byte wire header limit.");
            }

            /*
             * Small fragments stay on the stack to avoid renting a buffer.
             * This keeps the fast path allocation-free for fragments that fit
             * comfortably within the stack allocation limit.
             */
            if (totalFrameSize <= PacketConstants.StackAllocLimit)
            {
                Span<byte> frame = stackalloc byte[totalFrameSize];

                // Write the outer frame length.
                BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)totalFrameSize);

                // Write FragmentHeader + magic + chunk body.
                headerBuffer.CopyTo(frame[HeaderSize..]);
                payload.Slice(offset, thisChunkSize).CopyTo(frame[(HeaderSize + FragmentHeader.WireSize)..]);

                SendResult res = SEND_RAW_FRAME(frame);
                if (res != SendResult.Success)
                {
                    return res;
                }
            }
            else
            {
                /*
                 * Large fragments use a pooled buffer so we do not allocate on
                 * every chunk and can reuse the same memory pressure budget.
                 */
                byte[] rented = BufferLease.ByteArrayPool.Rent(totalFrameSize);
                try
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(MemoryExtensions.AsSpan(rented), (ushort)totalFrameSize);
                    headerBuffer.CopyTo(MemoryExtensions.AsSpan(rented, HeaderSize));
                    payload.Slice(offset, thisChunkSize).CopyTo(MemoryExtensions.AsSpan(rented, HeaderSize + FragmentHeader.WireSize));

                    SendResult res = SEND_RAW_FRAME(rented.AsSpan(0, totalFrameSize));
                    if (res != SendResult.Success)
                    {
                        return res;
                    }
                }
                finally
                {
                    BufferLease.ByteArrayPool.Return(rented);
                }
            }
        }

        this.INVOKE_POST_CALLBACK();
        return SendResult.Success;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        SendResult SEND_RAW_FRAME(ReadOnlySpan<byte> frame)
        {
            int sent = 0;
            SendResult result = SendResult.Success;
            lock (_sendLock)
            {
                try
                {
                    while (sent < frame.Length)
                    {
                        int n = _socket.Send(frame[sent..]);
                        if (n == 0)
                        {
                            result = SendResult.PeerClosed;
                            break;
                        }

                        sent += n;
                        _ = Interlocked.Add(ref _bytesSent, n);
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode is
                       SocketError.ConnectionReset or
                       SocketError.ConnectionAborted or
                       SocketError.Shutdown or
                       SocketError.OperationAborted)
                {
                    result = SendResult.Aborted;
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    result = SendResult.Failed;
                }
            }

            if (result != SendResult.Success)
            {
                this.CANCEL_RECEIVE_ONCE();
                this.INVOKE_CLOSE_ONCE();
            }
            return result;
        }
    }

    private ValueTask<SendResult> SEND_FRAGMENTED_ASYNC(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length > s_fragmentOptions.MaxPayloadSize)
        {
            return ValueTask.FromException<SendResult>(new ArgumentOutOfRangeException(nameof(payload),
                $"Payload exceeds maximum allowed size {s_fragmentOptions.MaxPayloadSize}"));
        }

        ushort streamId = FragmentStreamId.Next();
        int chunkBodySize = s_fragmentOptions.MaxChunkSize;
        int totalChunks = (payload.Length + chunkBodySize - 1) / chunkBodySize;

        if (totalChunks > ushort.MaxValue)
        {
            return ValueTask.FromException<SendResult>(new InternalErrorException(
                $"Fragmented payload requires {totalChunks} chunks, which exceeds the {ushort.MaxValue}-chunk wire header limit."));
        }

        return SEND_CHUNKS_ASYNC(this, payload, streamId, chunkBodySize, totalChunks, token);

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        static async ValueTask<SendResult> SEND_CHUNKS_ASYNC(SocketConnection self, ReadOnlyMemory<byte> payload, ushort streamId, int chunkBodySize, int totalChunks, CancellationToken token)
        {
            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * chunkBodySize;
                int chunkLen = Math.Min(chunkBodySize, payload.Length - offset);
                bool isLast = i == totalChunks - 1;

                int framePayloadLen = FragmentHeader.WireSize + chunkLen;
                int totalFrameLen = HeaderSize + framePayloadLen;

                if (totalFrameLen > ushort.MaxValue)
                {
                    throw new InternalErrorException(
                        $"Fragmented frame size {totalFrameLen} exceeds the {ushort.MaxValue}-byte wire header limit.");
                }

                byte[] rented = BufferLease.ByteArrayPool.Rent(totalFrameLen);

                try
                {
                    // Write header directly into the rented buffer to avoid Span across await.
                    FragmentHeader fragHeader = new(streamId, (ushort)i, (ushort)totalChunks, isLast);
                    fragHeader.WriteTo(rented.AsSpan(HeaderSize, FragmentHeader.WireSize));

                    BUILD_FRAGMENT_FRAME(rented.AsSpan(0, totalFrameLen), payload.Slice(offset, chunkLen).Span);

                    SendResult res = await SEND_RAW_FRAME_ASYNC(self, rented.AsMemory(0, totalFrameLen), token).ConfigureAwait(false);
                    if (res != SendResult.Success)
                    {
                        return res;
                    }
                }
                finally
                {
                    BufferLease.ByteArrayPool.Return(rented);
                }
            }

            self.INVOKE_POST_CALLBACK();
            return SendResult.Success;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void BUILD_FRAGMENT_FRAME(Span<byte> frame, ReadOnlySpan<byte> chunkBody)
        {
            // Write the outer frame length first because the receiver reads this
            // prefix before it can interpret the fragment header or payload body.
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);

            // Copy the chunk body into the wire frame immediately after the
            // fragment header (which was already written).
            chunkBody.CopyTo(frame[(HeaderSize + FragmentHeader.WireSize)..]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ValueTask<SendResult> SEND_RAW_FRAME_ASYNC(SocketConnection self, ReadOnlyMemory<byte> frame, CancellationToken token)
        {
            int sent = 0;
            while (sent < frame.Length)
            {
                ValueTask<int> vt = self._socket.SendAsync(frame[sent..], SocketFlags.None, token);
                if (vt.IsCompletedSuccessfully)
                {
                    int n = vt.Result;
                    if (n == 0)
                    {
                        self.CANCEL_RECEIVE_ONCE();
                        self.INVOKE_CLOSE_ONCE();
                        return ValueTask.FromResult(SendResult.PeerClosed);
                    }

                    sent += n;
                    _ = Interlocked.Add(ref self._bytesSent, n);
                }
                else
                {
                    return AWAIT_RAW_SEND(self, vt, frame, sent, token);
                }
            }

            return ValueTask.FromResult(SendResult.Success);

            [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
            static async ValueTask<SendResult> AWAIT_RAW_SEND(SocketConnection self, ValueTask<int> vt, ReadOnlyMemory<byte> frame, int sent, CancellationToken token)
            {
                try
                {
                    int n = await vt.ConfigureAwait(false);
                    if (n == 0)
                    {
                        self.CANCEL_RECEIVE_ONCE();
                        self.INVOKE_CLOSE_ONCE();
                        return SendResult.PeerClosed;
                    }

                    sent += n;
                    _ = Interlocked.Add(ref self._bytesSent, n);

                    while (sent < frame.Length)
                    {
                        n = await self._socket.SendAsync(frame[sent..], SocketFlags.None, token).ConfigureAwait(false);
                        if (n == 0)
                        {
                            self.CANCEL_RECEIVE_ONCE();
                            self.INVOKE_CLOSE_ONCE();
                            return SendResult.PeerClosed;
                        }

                        sent += n;
                        _ = Interlocked.Add(ref self._bytesSent, n);
                    }
                    return SendResult.Success;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is
                       SocketError.ConnectionReset or
                       SocketError.ConnectionAborted or
                       SocketError.Shutdown or
                       SocketError.OperationAborted)
                {
                    return SendResult.Aborted;
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    return SendResult.Failed;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void INVOKE_POST_CALLBACK() => _connectionOwner?.OnFrameSent();

    #endregion
}
