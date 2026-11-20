// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.Network.Internal;
using Nalix.Network.Internal.Connections;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.Connections;

/// <summary>
/// The TCP transport of a <see cref="Connection"/>. The connection is its own transport: it is created once per
/// connection and never pooled, so a caller that kept <see cref="Transport"/> cannot reach a later connection. Its
/// sequence counters are pooled with the connection's backing, and the socket is only reachable while the
/// connection is alive.
/// </summary>
public sealed partial class Connection : IConnection.ITransport, IConnection.ISocketTransport
{
    #region Transport Fields

    /// <summary>Replaces the counters of a closed connection so stale callers never touch the next owner's counters.</summary>
    private static readonly SequenceCounter s_detachedSequence = new();

    private SemaphoreSlim SendLock => _sendLockScope.Lock;

    // The scope handed out by AcquireSendLockAsync. A separate object, not the connection itself: if the connection
    // implemented IAsyncDisposable, `await using` (or any disposer that prefers DisposeAsync) would release the lock
    // instead of closing the connection. One per connection, as master allocated one per Initialize.
#pragma warning disable CA2213 // Holds only a SemaphoreSlim without AvailableWaitHandle: no unmanaged resource.
    private readonly SendLockScope _sendLockScope = new();
#pragma warning restore CA2213
    private TransportFraming _tcpFraming = TransportFraming.None;

    #endregion Transport Fields

    #region Transport Helpers

    /// <summary>
    /// Test seam: invoked after a send has captured its socket and generation, before the write is admitted.
    /// </summary>
    internal static Action<SocketConnection>? s_afterSendCaptureHook;

    /// <summary>
    /// Captures the socket of this connection's session together with its generation. The generation is read before
    /// <c>_backing</c> is checked again: <see cref="Dispose"/> clears <c>_backing</c> before the instance can be returned to
    /// the pool, so a passed check proves the generation belongs to this connection's session.
    /// </summary>
    private bool TryCaptureSendTarget(out SocketConnection socket, out int generation)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        SocketConnection? captured = backing?.Socket;
        generation = captured?.Generation ?? 0;
        if (captured is null || !ReferenceEquals(Volatile.Read(ref _backing), backing))
        {
            socket = null!;
            return false;
        }

        socket = captured;
        s_afterSendCaptureHook?.Invoke(captured);
        return true;
    }

    private SequenceCounter TcpSendSequence => Volatile.Read(ref _backing)?.SendSequence ?? s_detachedSequence;

    private SequenceCounter TcpReceiveSequence => Volatile.Read(ref _backing)?.ReceiveSequence ?? s_detachedSequence;

    /// <summary>
    /// Advances a TCP counter of this connection. The counter is read through the backing, and the backing is checked
    /// again before the advance: once the connection has closed, its backing may already belong to another connection,
    /// so a closed connection advances nothing. The window between the check and the advance is a few instructions.
    /// </summary>
    private uint NEXT_TCP_SEQUENCE(bool send)
    {
        ConnectionBacking? backing = Volatile.Read(ref _backing);
        if (backing is null)
        {
            return 0;
        }

        s_afterSequenceCaptureHook?.Invoke(backing);

        if (!ReferenceEquals(Volatile.Read(ref _backing), backing))
        {
            return 0;
        }

        return (send ? backing.SendSequence : backing.ReceiveSequence).Next();
    }

    /// <summary>Test seam: invoked after a sequence advance has read its backing, before the backing is checked again.</summary>
    internal static Action<ConnectionBacking>? s_afterSequenceCaptureHook;

    #endregion Transport Helpers

    #region IConnection.ISocketTransport

    /// <inheritdoc/>
    /// <remarks>
    /// The raw socket is needed by the tunneling extension, which unwraps the connection to pipe bytes directly.
    /// </remarks>
    System.Net.Sockets.Socket IConnection.ISocketTransport.Socket
        => (Volatile.Read(ref _backing)?.Socket ?? throw new ObjectDisposedException(nameof(Connection))).Socket;

    /// <inheritdoc/>
    Task? IConnection.ISocketTransport.ReceiveLoopTask => Volatile.Read(ref _backing)?.Socket?.ReceiveLoopTask;

    /// <inheritdoc/>
    byte[]? IConnection.ISocketTransport.StolenData => Volatile.Read(ref _backing)?.Socket?.StolenData;

    /// <inheritdoc/>
    System.Net.Sockets.Socket IConnection.ISocketTransport.Unwrap()
        => (Volatile.Read(ref _backing)?.Socket ?? throw new ObjectDisposedException(nameof(Connection))).Unwrap();

    #endregion IConnection.ISocketTransport

    #region IConnection.ITransport

    /// <inheritdoc/>
    TransportFraming IConnection.ITransport.Framing => _tcpFraming;

    /// <inheritdoc/>
    ISequenceCounter ITransportSequencer.SendSequence => this.TcpSendSequence;

    /// <inheritdoc/>
    ISequenceCounter ITransportSequencer.ReceiveSequence => this.TcpReceiveSequence;

    /// <inheritdoc/>
    uint ITransportSequencer.NextSendSequence() => this.NEXT_TCP_SEQUENCE(send: true);

    /// <inheritdoc/>
    uint ITransportSequencer.NextReceiveSequence() => this.NEXT_TCP_SEQUENCE(send: false);

    /// <inheritdoc/>
    uint ITransportSequencer.CurrentSendSequence => this.TcpSendSequence.Current();

    /// <inheritdoc/>
    uint ITransportSequencer.CurrentReceiveSequence => this.TcpReceiveSequence.Current();

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IConnection.ITransport.Send(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty)
        {
            throw new ArgumentException("Message must not be empty.", nameof(message));
        }

        ObjectDisposedException.ThrowIf(!this.TryCaptureSendTarget(out SocketConnection socket, out int generation), typeof(Connection));

        SocketConnection.SendResult result = socket.Send(message, generation);

        if (result is SocketConnection.SendResult.PeerClosed or SocketConnection.SendResult.Aborted)
        {
            // Connection is already closed/closing. Swallow the error to prevent throw overhead.
            return;
        }
        else if (result != SocketConnection.SendResult.Success)
        {
            throw SEND_FAILURE(result);
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ValueTask IConnection.ITransport.SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (message.IsEmpty)
        {
            return ValueTask.FromException(new ArgumentException("Message must not be empty.", nameof(message)));
        }

        if (!this.TryCaptureSendTarget(out SocketConnection socket, out int generation))
        {
            return ValueTask.FromException(new ObjectDisposedException(nameof(Connection)));
        }

        return this.SEND_ASYNC(socket, generation, message, cancellationToken, acquireLock: true);
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ValueTask<IAsyncDisposable> IConnection.ITransport.AcquireSendLockAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && this.SendLock.Wait(0, cancellationToken))
        {
            return new ValueTask<IAsyncDisposable>(_sendLockScope.Enter());
        }

        return this.ACQUIRE_SEND_LOCK_ASYNC(cancellationToken);
    }

    private async ValueTask<IAsyncDisposable> ACQUIRE_SEND_LOCK_ASYNC(CancellationToken cancellationToken)
    {
        await this.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _sendLockScope.Enter();
    }

    /// <summary>
    /// Releases the send lock taken by <see cref="IConnection.ITransport.AcquireSendLockAsync"/>. Only the first dispose
    /// of an acquisition releases the lock; a repeated dispose is a no-op.
    /// </summary>
    private sealed class SendLockScope : IAsyncDisposable
    {
#pragma warning disable CA2213 // A SemaphoreSlim without AvailableWaitHandle holds no unmanaged resource.
        public readonly SemaphoreSlim Lock = new(1, 1);
#pragma warning restore CA2213

        // 1 while the lock was handed out through this scope and the scope has not been disposed yet.
        private int _held;

        public SendLockScope Enter()
        {
            Volatile.Write(ref _held, 1);
            return this;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _held, 0) == 1)
            {
                _ = this.Lock.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    ValueTask IConnection.ITransport.SendAsyncCore(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (message.IsEmpty)
        {
            return ValueTask.FromException(new ArgumentException("Message must not be empty.", nameof(message)));
        }

        if (!this.TryCaptureSendTarget(out SocketConnection socket, out int generation))
        {
            return ValueTask.FromException(new ObjectDisposedException(nameof(Connection)));
        }

        return this.SEND_ASYNC(socket, generation, message, cancellationToken, acquireLock: false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// When the lease reserved <see cref="BufferLease.TransportHeadroom"/> bytes in front of the payload,
    /// the 2-byte length prefix is written into that space and the frame is sent in place, skipping the
    /// rent + copy that <see cref="SocketConnection.SendAsync(ReadOnlyMemory{byte}, int, CancellationToken)"/> needs.
    /// </remarks>
    ValueTask IConnection.ITransport.SendAsyncCore(IBufferLease frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!this.TryCaptureSendTarget(out SocketConnection socket, out int generation))
        {
            return ValueTask.FromException(new ObjectDisposedException(nameof(Connection)));
        }

        if (frame is BufferLease lease
            && socket.CanSendWithReservedHeader(lease.Length)
            && lease.TryGetSegmentWithHeader(BufferLease.TransportHeadroom, out ArraySegment<byte> segment))
        {
            return COMPLETE_SEND(socket.SendWithReservedHeaderAsync(segment.Array!, segment.Offset, lease.Length, generation, cancellationToken));
        }

        return this.SEND_ASYNC(socket, generation, frame.Memory, cancellationToken, acquireLock: false);

        static ValueTask COMPLETE_SEND(ValueTask<SocketConnection.SendResult> vt)
        {
            if (vt.IsCompletedSuccessfully)
            {
                SocketConnection.SendResult result = vt.Result;
                return result is SocketConnection.SendResult.Success
                              or SocketConnection.SendResult.PeerClosed
                              or SocketConnection.SendResult.Aborted
                    ? default
                    : ValueTask.FromException(SEND_FAILURE(result));
            }

            return AWAIT(vt);

            static async ValueTask AWAIT(ValueTask<SocketConnection.SendResult> vt)
            {
                SocketConnection.SendResult result = await vt.ConfigureAwait(false);
                if (result != SocketConnection.SendResult.Success)
                {
                    throw SEND_FAILURE(result);
                }
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Frames that fit a single UInt16-prefixed frame are written into one buffer and sent with one socket
    /// write per <see cref="SocketConnection.MaxBatchBytes"/>; any other batch is sent frame by frame.
    /// </remarks>
    ValueTask IConnection.ITransport.SendAsyncCore(ReadOnlyMemory<IBufferLease> frames, CancellationToken cancellationToken)
    {
        if (frames.Length > 1
            && this.TryCaptureSendTarget(out SocketConnection socket, out int generation)
            && socket.CanSendBatch(frames.Span))
        {
            return COMPLETE_SEND(socket.SendBatchAsync(frames, generation, cancellationToken));
        }

        return SEND_SEQUENTIAL_ASYNC(this, frames, cancellationToken);

        static async ValueTask SEND_SEQUENTIAL_ASYNC(IConnection.ITransport transport, ReadOnlyMemory<IBufferLease> frames, CancellationToken cancellationToken)
        {
            for (int i = 0; i < frames.Length; i++)
            {
                await transport.SendAsyncCore(frames.Span[i], cancellationToken).ConfigureAwait(false);
            }
        }

        static async ValueTask COMPLETE_SEND(ValueTask<SocketConnection.SendResult> vt)
        {
            SocketConnection.SendResult result = await vt.ConfigureAwait(false);
            if (result is not (SocketConnection.SendResult.Success
                            or SocketConnection.SendResult.PeerClosed
                            or SocketConnection.SendResult.Aborted))
            {
                throw SEND_FAILURE(result);
            }
        }
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask SEND_ASYNC(SocketConnection socket, int generation, ReadOnlyMemory<byte> message, CancellationToken cancellationToken, bool acquireLock)
    {
        if (acquireLock)
        {
            return this.SEND_WITH_LOCK_ASYNC(socket, generation, message, cancellationToken);
        }

        ValueTask<SocketConnection.SendResult> vt = socket.SendAsync(message, generation, cancellationToken);
        if (vt.IsCompletedSuccessfully)
        {
            SocketConnection.SendResult result = vt.Result;

            if (result is SocketConnection.SendResult.PeerClosed or SocketConnection.SendResult.Aborted)
            {
                // Connection is already closed/closing. Swallow the error to prevent throw overhead.
                return default;
            }
            else if (result != SocketConnection.SendResult.Success)
            {
                return ValueTask.FromException(SEND_FAILURE(result));
            }

            return default;
        }

        return AWAIT_SEND_ASYNC(vt);

        static async ValueTask AWAIT_SEND_ASYNC(ValueTask<SocketConnection.SendResult> vt)
        {
            SocketConnection.SendResult result = await vt.ConfigureAwait(false);
            if (result != SocketConnection.SendResult.Success)
            {
                throw SEND_FAILURE(result);
            }
        }
    }

    /// <summary>
    /// The exception for a send that did not succeed. A send refused because the session already ended reports the
    /// disposed state, as the connection does before its transport is used after close.
    /// </summary>
    private static Exception SEND_FAILURE(SocketConnection.SendResult result)
        => result == SocketConnection.SendResult.SessionClosed
            ? new ObjectDisposedException(nameof(Connection))
            : Throw.GetSendFailed();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask SEND_WITH_LOCK_ASYNC(SocketConnection socket, int generation, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && this.SendLock.Wait(0, cancellationToken))
        {
            ValueTask vt;
            try
            {
                vt = this.SEND_ASYNC(socket, generation, message, cancellationToken, acquireLock: false);
            }
            catch
            {
                _ = this.SendLock.Release();
                throw;
            }

            if (vt.IsCompletedSuccessfully)
            {
                _ = this.SendLock.Release();
                return default;
            }

            return AWAIT_WITH_RELEASE_ASYNC(this.SendLock, vt);
        }

        return AWAIT_ACQUIRE_AND_SEND_ASYNC(this, socket, generation, message, cancellationToken);

        static async ValueTask AWAIT_WITH_RELEASE_ASYNC(SemaphoreSlim sendLock, ValueTask vt)
        {
            try
            {
                await vt.ConfigureAwait(false);
            }
            finally
            {
                _ = sendLock.Release();
            }
        }

        static async ValueTask AWAIT_ACQUIRE_AND_SEND_ASYNC(Connection connection, SocketConnection socket, int generation, ReadOnlyMemory<byte> msg, CancellationToken ct)
        {
            await connection.SendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.SEND_ASYNC(socket, generation, msg, ct, acquireLock: false).ConfigureAwait(false);
            }
            finally
            {
                _ = connection.SendLock.Release();
            }
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IConnection.ITransport.BeginReceive(CancellationToken cancellationToken)
    {
        SocketConnection? socket = Volatile.Read(ref _backing)?.Socket;
        ObjectDisposedException.ThrowIf(socket is null || this.IsDisposed, typeof(Connection));
        socket.BeginReceive(cancellationToken);
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IConnection.ITransport.UseFraming(TransportFraming framing)
    {
        _tcpFraming = framing;
        SocketConnection? socket = Volatile.Read(ref _backing)?.Socket;
        ObjectDisposedException.ThrowIf(socket is null, typeof(Connection));
        socket.SetFraming(framing);
    }

    #endregion IConnection.ITransport
}
