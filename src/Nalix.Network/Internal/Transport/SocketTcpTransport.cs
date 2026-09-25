// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.Network.Connections;

[assembly: InternalsVisibleTo("Nalix.Network.Tests")]
[assembly: InternalsVisibleTo("Nalix.Network.Benchmarks")]

namespace Nalix.Network.Internal.Transport;

[SkipLocalsInit]
[DebuggerNonUserCode]
[EditorBrowsable(EditorBrowsableState.Never)]
internal sealed class SocketTcpTransport : IConnection.ISocketTransport, IPoolable, IDisposable
{
    #region Fields

    private Connection? _outer;
    private SocketConnection? _socket;
    private readonly SequenceCounter _sendSequence = new();
    private readonly SequenceCounter _receiveSequence = new();
    private SemaphoreSlim _sendLock = null!;

    #endregion Fields

    #region Constructor

    public SocketTcpTransport()
    {
    }

    #endregion Constructor

    #region Properties

    /// <inheritdoc/>
    public TransportFraming Framing { get; private set; } = TransportFraming.None;

    /// <inheritdoc/>
    public Socket Socket => _socket?.Socket ?? throw new ObjectDisposedException(nameof(SocketTcpTransport));

    /// <inheritdoc/>
    public Task? ReceiveLoopTask => _socket?.ReceiveLoopTask;

    /// <inheritdoc/>
    public byte[]? StolenData => _socket?.StolenData;

    /// <inheritdoc/>
    public ISequenceCounter SendSequence => _sendSequence;

    /// <inheritdoc/>
    public ISequenceCounter ReceiveSequence => _receiveSequence;

    #endregion Properties

    #region Methods

    /// <inheritdoc/>
    public void Initialize(Connection outer, SocketConnection socket)
    {
        _outer = outer ?? throw new ArgumentNullException(nameof(outer));
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _sendLock = new SemaphoreSlim(1, 1);
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Socket Unwrap()
    {
        ObjectDisposedException.ThrowIf(_socket is null, typeof(SocketTcpTransport));
        return _socket.Unwrap();
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginReceive(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_outer is null || _socket is null, typeof(SocketTcpTransport));
        ObjectDisposedException.ThrowIf(_outer.IsDisposed, nameof(Connection));
        _socket.BeginReceive(cancellationToken);
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UseFraming(TransportFraming framing)
    {
        this.Framing = framing;
        ObjectDisposedException.ThrowIf(_socket is null, typeof(SocketTcpTransport));
        _socket.SetFraming(framing);
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Send(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty)
        {
            throw new ArgumentException("Message must not be empty.", nameof(message));
        }

        ObjectDisposedException.ThrowIf(_socket is null, typeof(SocketTcpTransport));

        SocketConnection.SendResult result = _socket.Send(message);

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
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        => this.SEND_ASYNC(message, cancellationToken, acquireLock: true);

    /// <inheritdoc/>
    ValueTask<IAsyncDisposable> IConnection.ITransport.AcquireSendLockAsync(CancellationToken cancellationToken)
        => this.ACQUIRE_SEND_LOCK_ASYNC(cancellationToken);

    private async ValueTask<IAsyncDisposable> ACQUIRE_SEND_LOCK_ASYNC(CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SendLockScope(_sendLock);
    }

    private readonly struct SendLockScope(SemaphoreSlim sendLock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            _ = sendLock.Release();
            return ValueTask.CompletedTask;
        }
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

        SocketConnection? socket = _socket;
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

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask SEND_ASYNC(ReadOnlyMemory<byte> message, CancellationToken cancellationToken, bool acquireLock)
    {
        if (message.IsEmpty)
        {
            return ValueTask.FromException(new ArgumentException("Message must not be empty.", nameof(message)));
        }

        if (_socket is null)
        {
            return ValueTask.FromException(new ObjectDisposedException(nameof(SocketTcpTransport)));
        }

        if (acquireLock)
        {
            return this.SEND_WITH_LOCK_ASYNC(message, cancellationToken);
        }

        ValueTask<SocketConnection.SendResult> vt = _socket.SendAsync(message, cancellationToken);
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

    private async ValueTask SEND_WITH_LOCK_ASYNC(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.SEND_ASYNC(message, cancellationToken, acquireLock: false).ConfigureAwait(false);
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextSendSequence() => _sendSequence.Next();

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextReceiveSequence() => _receiveSequence.Next();

    /// <inheritdoc/>
    public uint CurrentSendSequence => _sendSequence.Current();

    /// <inheritdoc/>
    public uint CurrentReceiveSequence => _receiveSequence.Current();

    #endregion Methods

    #region Pooling

    /// <inheritdoc/>
    public void ResetForPool()
    {
        _outer = null;
        _socket = null;
        _sendLock?.Dispose();
        _sendLock = null!;
        _sendSequence.Reset(0);
        _receiveSequence.Reset(0);
        this.Framing = TransportFraming.None;
    }

    /// <inheritdoc/>
    public void Dispose() => this.ResetForPool();

    #endregion Pooling
}
