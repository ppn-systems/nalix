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
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.Connections;

/// <summary>
/// The TCP transport of a <see cref="Connection"/>. The connection is its own transport: it is created once per
/// connection and never pooled, so a caller that kept <see cref="TCP"/> cannot reach a later connection. Its
/// sequence counters are pooled with the connection's backing, and the socket is only reachable while the
/// connection is alive.
/// </summary>
public sealed partial class Connection : IConnection.ITransport, IAsyncDisposable
{
    #region Transport Fields

    /// <summary>Replaces the counters of a closed connection so stale callers never touch the next owner's counters.</summary>
    private static readonly SequenceCounter s_detachedSequence = new();

#pragma warning disable CA2213 // A SemaphoreSlim without AvailableWaitHandle holds no unmanaged resource; release is via the connection's scope.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
#pragma warning restore CA2213
    private TransportFraming _tcpFraming = TransportFraming.None;

    #endregion Transport Fields

    #region Transport Helpers

    private SocketConnection? TcpSocket => Volatile.Read(ref _backing)?.Socket;

    private SequenceCounter TcpSendSequence => Volatile.Read(ref _backing)?.SendSequence ?? s_detachedSequence;

    private SequenceCounter TcpReceiveSequence => Volatile.Read(ref _backing)?.ReceiveSequence ?? s_detachedSequence;

    /// <summary>Gets the UDP send counter of this connection's backing; used when a UDP binding is created.</summary>
    internal SequenceCounter UdpSendSequenceCounter
        => (Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(Connection))).UdpSendSequence;

    /// <summary>Gets the UDP receive counter of this connection's backing; used when a UDP binding is created.</summary>
    internal SequenceCounter UdpReceiveSequenceCounter
        => (Volatile.Read(ref _backing) ?? throw new ObjectDisposedException(nameof(Connection))).UdpReceiveSequence;

    #endregion Transport Helpers

    #region IConnection.ITransport

    /// <inheritdoc/>
    TransportFraming IConnection.ITransport.Framing => _tcpFraming;

    /// <inheritdoc/>
    ISequenceCounter ITransportSequencer.SendSequence => this.TcpSendSequence;

    /// <inheritdoc/>
    ISequenceCounter ITransportSequencer.ReceiveSequence => this.TcpReceiveSequence;

    /// <inheritdoc/>
    uint ITransportSequencer.NextSendSequence() => this.TcpSendSequence.Next();

    /// <inheritdoc/>
    uint ITransportSequencer.NextReceiveSequence() => this.TcpReceiveSequence.Next();

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

        SocketConnection? socket = this.TcpSocket;
        ObjectDisposedException.ThrowIf(socket is null, typeof(Connection));

        SocketConnection.SendResult result = socket.Send(message);

        if (result is SocketConnection.SendResult.PeerClosed or SocketConnection.SendResult.Aborted)
        {
            // Connection is already closed/closing. Swallow the error to prevent throw overhead.
            return;
        }
        else if (result != SocketConnection.SendResult.Success)
        {
            throw Throw.GetSendFailed();
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ValueTask IConnection.ITransport.SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        => this.SEND_ASYNC(message, cancellationToken, acquireLock: true);

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ValueTask<IAsyncDisposable> IConnection.ITransport.AcquireSendLockAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && _sendLock.Wait(0, cancellationToken))
        {
            return new ValueTask<IAsyncDisposable>(this);
        }

        return this.ACQUIRE_SEND_LOCK_ASYNC(cancellationToken);
    }

    private async ValueTask<IAsyncDisposable> ACQUIRE_SEND_LOCK_ASYNC(CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return this;
    }

    /// <summary>
    /// Releases the send lock taken by <see cref="IConnection.ITransport.AcquireSendLockAsync"/>. The connection
    /// itself is the scope, so acquiring the lock allocates nothing.
    /// </summary>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        _ = _sendLock.Release();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    ValueTask IConnection.ITransport.SendAsyncCore(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        => this.SEND_ASYNC(message, cancellationToken, acquireLock: false);

    /// <inheritdoc/>
    /// <remarks>
    /// When the lease reserved <see cref="BufferLease.TransportHeadroom"/> bytes in front of the payload,
    /// the 2-byte length prefix is written into that space and the frame is sent in place, skipping the
    /// rent + copy that <see cref="SocketConnection.SendAsync(ReadOnlyMemory{byte}, CancellationToken)"/> needs.
    /// </remarks>
    ValueTask IConnection.ITransport.SendAsyncCore(IBufferLease frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        SocketConnection? socket = this.TcpSocket;
        if (socket is not null
            && frame is BufferLease lease
            && socket.CanSendWithReservedHeader(lease.Length)
            && lease.TryGetSegmentWithHeader(BufferLease.TransportHeadroom, out ArraySegment<byte> segment))
        {
            return COMPLETE_SEND(socket.SendWithReservedHeaderAsync(segment.Array!, segment.Offset, lease.Length, cancellationToken));
        }

        return this.SEND_ASYNC(frame.Memory, cancellationToken, acquireLock: false);

        static ValueTask COMPLETE_SEND(ValueTask<SocketConnection.SendResult> vt)
        {
            if (vt.IsCompletedSuccessfully)
            {
                SocketConnection.SendResult result = vt.Result;
                return result is SocketConnection.SendResult.Success
                              or SocketConnection.SendResult.PeerClosed
                              or SocketConnection.SendResult.Aborted
                    ? default
                    : ValueTask.FromException(Throw.GetSendFailed());
            }

            return AWAIT(vt);

            static async ValueTask AWAIT(ValueTask<SocketConnection.SendResult> vt)
            {
                SocketConnection.SendResult result = await vt.ConfigureAwait(false);
                if (result != SocketConnection.SendResult.Success)
                {
                    throw Throw.GetSendFailed();
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
        SocketConnection? socket = this.TcpSocket;
        if (socket is not null && frames.Length > 1 && socket.CanSendBatch(frames.Span))
        {
            return COMPLETE_SEND(socket.SendBatchAsync(frames, cancellationToken));
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
                throw Throw.GetSendFailed();
            }
        }
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask SEND_ASYNC(ReadOnlyMemory<byte> message, CancellationToken cancellationToken, bool acquireLock)
    {
        if (message.IsEmpty)
        {
            return ValueTask.FromException(new ArgumentException("Message must not be empty.", nameof(message)));
        }

        SocketConnection? socket = this.TcpSocket;
        if (socket is null)
        {
            return ValueTask.FromException(new ObjectDisposedException(nameof(Connection)));
        }

        if (acquireLock)
        {
            return this.SEND_WITH_LOCK_ASYNC(message, cancellationToken);
        }

        ValueTask<SocketConnection.SendResult> vt = socket.SendAsync(message, cancellationToken);
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
                return ValueTask.FromException(Throw.GetSendFailed());
            }

            return default;
        }

        return AWAIT_SEND_ASYNC(vt);

        static async ValueTask AWAIT_SEND_ASYNC(ValueTask<SocketConnection.SendResult> vt)
        {
            SocketConnection.SendResult result = await vt.ConfigureAwait(false);
            if (result != SocketConnection.SendResult.Success)
            {
                throw Throw.GetSendFailed();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask SEND_WITH_LOCK_ASYNC(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && _sendLock.Wait(0, cancellationToken))
        {
            ValueTask vt;
            try
            {
                vt = this.SEND_ASYNC(message, cancellationToken, acquireLock: false);
            }
            catch
            {
                _ = _sendLock.Release();
                throw;
            }

            if (vt.IsCompletedSuccessfully)
            {
                _ = _sendLock.Release();
                return default;
            }

            return AWAIT_WITH_RELEASE_ASYNC(_sendLock, vt);
        }

        return AWAIT_ACQUIRE_AND_SEND_ASYNC(this, message, cancellationToken);

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

        static async ValueTask AWAIT_ACQUIRE_AND_SEND_ASYNC(Connection connection, ReadOnlyMemory<byte> msg, CancellationToken ct)
        {
            await connection._sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.SEND_ASYNC(msg, ct, acquireLock: false).ConfigureAwait(false);
            }
            finally
            {
                _ = connection._sendLock.Release();
            }
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IConnection.ITransport.BeginReceive(CancellationToken cancellationToken)
    {
        SocketConnection? socket = this.TcpSocket;
        ObjectDisposedException.ThrowIf(socket is null || this.IsDisposed, typeof(Connection));
        socket.BeginReceive(cancellationToken);
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IConnection.ITransport.UseFraming(TransportFraming framing)
    {
        _tcpFraming = framing;
        SocketConnection? socket = this.TcpSocket;
        ObjectDisposedException.ThrowIf(socket is null, typeof(Connection));
        socket.SetFraming(framing);
    }

    #endregion IConnection.ITransport
}
