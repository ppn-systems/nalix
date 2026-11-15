// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Environment.Memory;

namespace Nalix.Network.Internal.Transport;

internal sealed partial class SocketConnection
{
    private SendResult SEND_VARINT(ReadOnlySpan<byte> data, int generation)
    {
        int varIntSize = Leb128.GetByteCount(data.Length);
        long totalLengthLong = (long)data.Length + varIntSize;

        if (totalLengthLong > _maxVarIntPayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                totalLengthLong,
                $"VarInt payload exceeds the maximum allowed size of {_maxVarIntPayloadSize}.");
        }

        int totalLength = (int)totalLengthLong;

        if (totalLength <= PacketConstants.StackAllocLimit)
        {
            Span<byte> frameS = stackalloc byte[totalLength];
            WRITE_VARINT_FRAME_HEADER(frameS, data.Length, varIntSize, data);

            int sent = 0;
            SendResult result = SendResult.Success;
            lock (_sendLock)
            {
                // Checked under the write lock: Dispose takes this lock before the instance can be returned,
                // so a write either completes before the session ends or observes that it has.
                if (!this.IS_CURRENT_SESSION(generation))
                {
                    return SendResult.Aborted;
                }

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

        byte[] heapBuf = BufferLease.ByteArrayPool.Rent(totalLength);
        try
        {
            WRITE_VARINT_FRAME_HEADER(MemoryExtensions.AsSpan(heapBuf), data.Length, varIntSize, data);

            int sent = 0;
            SendResult result = SendResult.Success;
            lock (_sendLock)
            {
                // Checked under the write lock: Dispose takes this lock before the instance can be returned,
                // so a write either completes before the session ends or observes that it has.
                if (!this.IS_CURRENT_SESSION(generation))
                {
                    return SendResult.Aborted;
                }

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

    private ValueTask<SendResult> SEND_VARINT_ASYNC(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        int varIntSize = Leb128.GetByteCount(data.Length);
        long totalLengthLong = (long)data.Length + varIntSize;

        if (totalLengthLong > _maxVarIntPayloadSize)
        {
            return ValueTask.FromException<SendResult>(new ArgumentOutOfRangeException(
                nameof(data),
                totalLengthLong,
                $"VarInt payload exceeds the maximum allowed size of {_maxVarIntPayloadSize}."));
        }

        int totalLength = (int)totalLengthLong;
        byte[] heapBuf = BufferLease.ByteArrayPool.Rent(totalLength);

        try
        {
            WRITE_VARINT_FRAME_HEADER(MemoryExtensions.AsSpan(heapBuf), data.Length, varIntSize, data.Span);

            int sent = 0;
            while (sent < totalLength)
            {
                ValueTask<int> vt = _socket.SendAsync(MemoryExtensions.AsMemory(heapBuf, sent, totalLength - sent), SocketFlags.None, cancellationToken);
                if (vt.IsCompletedSuccessfully)
                {
                    int n = vt.Result;
                    if (n == 0)
                    {
                        BufferLease.ByteArrayPool.Return(heapBuf);
                        this.CANCEL_RECEIVE_ONCE();
                        this.INVOKE_CLOSE_ONCE();
                        return ValueTask.FromResult(SendResult.PeerClosed);
                    }
                    sent += n;
                    _ = Interlocked.Add(ref _bytesSent, n);
                }
                else
                {
                    return AWAIT_VARINT_SEND(this, vt, heapBuf, sent, totalLength, cancellationToken);
                }
            }

            this.INVOKE_POST_CALLBACK();
            BufferLease.ByteArrayPool.Return(heapBuf);
            return ValueTask.FromResult(SendResult.Success);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is
               SocketError.ConnectionReset or
               SocketError.ConnectionAborted or
               SocketError.Shutdown or
               SocketError.OperationAborted)
        {
            BufferLease.ByteArrayPool.Return(heapBuf);
            return ValueTask.FromResult(SendResult.Aborted);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            BufferLease.ByteArrayPool.Return(heapBuf);
            return ValueTask.FromResult(SendResult.Failed);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        static async ValueTask<SendResult> AWAIT_VARINT_SEND(SocketConnection self, ValueTask<int> vt, byte[] heapBuf, int sent, int totalLength, CancellationToken token)
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
                    n = await self._socket.SendAsync(MemoryExtensions.AsMemory(heapBuf, sent, totalLength - sent), SocketFlags.None, token).ConfigureAwait(false);
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
                BufferLease.ByteArrayPool.Return(heapBuf);
            }
        }
    }

    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WRITE_VARINT_FRAME_HEADER(Span<byte> buffer, int payloadLength, int varIntSize, ReadOnlySpan<byte> payload)
    {
        _ = Leb128.Write(buffer, payloadLength);
        payload.CopyTo(buffer[varIntSize..]);
    }
}
