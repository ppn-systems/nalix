// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;
using Nalix.Environment.Sequencing;
using Nalix.Network.Internal;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

namespace Nalix.Network.Connections;

/// <summary>
/// The WebSocket transport of a <see cref="WebSocketConnection"/>. The connection is its own transport: it is created
/// once per connection and never pooled, so a caller that kept <see cref="TCP"/> cannot reach a later connection.
/// Its sequence counters are pooled with the connection's backing; after the connection is disposed every send is a
/// no-op.
/// </summary>
public sealed partial class WebSocketConnection : IConnection.ITransport, IAsyncDisposable
{
    #region Transport Fields

    /// <summary>Replaces the counters of a closed connection so stale callers never touch the next owner's counters.</summary>
    private static readonly SequenceCounter s_detachedSequence = new();

    private static readonly FragmentOptions s_fragmentOptions = ConfigurationManager.Instance.Get<FragmentOptions>();

    private WebSocket _webSocket;
    private CoalescingWriteStream? _writeBatcher;
    private NetworkWebSocketOptions _wsOptions;
#pragma warning disable CA2213 // A SemaphoreSlim without AvailableWaitHandle holds no unmanaged resource; release is via the connection's scope.
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);
#pragma warning restore CA2213

    private int _transportDisposed;
    private int _receiveStarted;
    private Task? _receiveLoopTask;

    #endregion Transport Fields

    #region Transport Helpers

    private SequenceCounter WsSendSequence => Volatile.Read(ref _backing)?.SendSequence ?? s_detachedSequence;

    private SequenceCounter WsReceiveSequence => Volatile.Read(ref _backing)?.ReceiveSequence ?? s_detachedSequence;

    /// <summary>Gets the underlying WebSocket of this connection.</summary>
    internal WebSocket WebSocket => _webSocket;

    /// <summary>Gets the lock that serializes sends on the WebSocket.</summary>
    internal SemaphoreSlim SendLock => _wsSendLock;

    /// <summary>
    /// Wires the WebSocket into this connection. Called once from the constructor.
    /// </summary>
    private void InitializeTransport(WebSocket webSocket, CoalescingWriteStream? writeBatcher)
    {
        _writeBatcher = writeBatcher;
        _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _wsOptions = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
    }

    #endregion Transport Helpers

    #region IConnection.ITransport

    /// <inheritdoc/>
    TransportFraming IConnection.ITransport.Framing => TransportFraming.None;

    /// <inheritdoc/>
    ISequenceCounter ITransportSequencer.SendSequence => this.WsSendSequence;

    /// <inheritdoc/>
    ISequenceCounter ITransportSequencer.ReceiveSequence => this.WsReceiveSequence;

    /// <inheritdoc/>
    uint ITransportSequencer.NextSendSequence() => this.WsSendSequence.Next();

    /// <inheritdoc/>
    uint ITransportSequencer.NextReceiveSequence() => this.WsReceiveSequence.Next();

    /// <inheritdoc/>
    uint ITransportSequencer.CurrentSendSequence => this.WsSendSequence.Current();

    /// <inheritdoc/>
    uint ITransportSequencer.CurrentReceiveSequence => this.WsReceiveSequence.Current();

    /// <inheritdoc/>
    [StackTraceHidden]
    void IConnection.ITransport.Send(ReadOnlySpan<byte> message) => Throw.WebSocketSyncSendNotSupported();

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ValueTask IConnection.ITransport.SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        // Peer already closed/closing -- swallow here (same as the TCP transport's
        // PeerClosed/Aborted swallow) instead of throwing, so callers several layers up
        // (dispatch/control-send paths) don't have to catch-and-classify this by exception
        // message string.
        if (this.IsDisposed || _webSocket.State != WebSocketState.Open)
        {
            return default;
        }

        // WebSockets handle framing natively, so we just send the message as binary.
        // A SemaphoreSlim is used because WebSocket.SendAsync doesn't support concurrent calls.
        if (!cancellationToken.IsCancellationRequested && _wsSendLock.Wait(0, cancellationToken))
        {
            ValueTask vt;
            try
            {
                vt = this.SEND_CORE_ASYNC(message, cancellationToken);
            }
            catch
            {
                _ = _wsSendLock.Release();
                throw;
            }

            if (vt.IsCompletedSuccessfully)
            {
                _ = _wsSendLock.Release();
                return default;
            }

            return AWAIT_WITH_RELEASE_ASYNC(_wsSendLock, vt);
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

        static async ValueTask AWAIT_ACQUIRE_AND_SEND_ASYNC(WebSocketConnection connection, ReadOnlyMemory<byte> msg, CancellationToken ct)
        {
            await connection._wsSendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.SEND_CORE_ASYNC(msg, ct).ConfigureAwait(false);
            }
            finally
            {
                _ = connection._wsSendLock.Release();
            }
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ValueTask<IAsyncDisposable> IConnection.ITransport.AcquireSendLockAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && _wsSendLock.Wait(0, cancellationToken))
        {
            return new ValueTask<IAsyncDisposable>(this);
        }

        return this.ACQUIRE_SEND_LOCK_ASYNC(cancellationToken);
    }

    private async ValueTask<IAsyncDisposable> ACQUIRE_SEND_LOCK_ASYNC(CancellationToken cancellationToken)
    {
        await _wsSendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return this;
    }

    /// <summary>
    /// Releases the send lock taken by <see cref="IConnection.ITransport.AcquireSendLockAsync"/>. The connection
    /// itself is the scope, so acquiring the lock allocates nothing.
    /// </summary>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        _ = _wsSendLock.Release();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    ValueTask IConnection.ITransport.SendAsyncCore(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (this.IsDisposed || _webSocket.State != WebSocketState.Open)
        {
            return ValueTask.CompletedTask;
        }

        return this.SEND_CORE_ASYNC(message, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every frame is still its own WebSocket message. When the connection's stream supports it, all
    /// messages but the last are held back so the batch reaches the socket in one write.
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    async ValueTask IConnection.ITransport.SendAsyncCore(ReadOnlyMemory<IBufferLease> frames, CancellationToken cancellationToken)
    {
        CoalescingWriteStream? batcher = frames.Length > 1 ? _writeBatcher : null;
        int last = frames.Length - 1;

        bool completed = false;

        batcher?.Cork();
        try
        {
            for (int i = 0; i <= last; i++)
            {
                if (this.IsDisposed || _webSocket.State != WebSocketState.Open)
                {
                    return;
                }

                if (i == last)
                {
                    batcher?.Uncork();
                }

                await this.SEND_CORE_ASYNC(frames.Span[i].Memory, cancellationToken).ConfigureAwait(false);
            }

            completed = true;
        }
        finally
        {
            if (batcher is not null)
            {
                batcher.Uncork();

                if (!completed)
                {
                    // The batch stopped before its final write, which would have carried the held-back
                    // messages (and anything the WebSocket itself wrote meanwhile, such as a close frame).
                    try
                    {
                        await batcher.FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                    {
                    }
                }
            }
        }
    }

    private async ValueTask SEND_CORE_ASYNC(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        await _webSocket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);

        this.AddBytesSent(message.Length);
        this.TriggerPostProcessEvent();
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    void IConnection.ITransport.BeginReceive(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.IsDisposed, nameof(WebSocketConnection));

        if (Interlocked.CompareExchange(ref _receiveStarted, 1, 0) != 0)
        {
            return;
        }

        _receiveLoopTask = this.RECEIVE_LOOP_ASYNC(cancellationToken);
    }

    /// <inheritdoc/>
    void IConnection.ITransport.UseFraming(TransportFraming framing)
    {
        // TODO: Review framing behavior. WebSocket currently relies on its built-in message framing.
    }

    #endregion IConnection.ITransport

    #region Receive Loop

    private static int GET_RECEIVE_BUFFER_SIZE()
    {
        if (s_fragmentOptions.MaxChunkSize <= 0)
        {
            Throw.WebSocketInvalidReceiveBuffer();
        }

        return s_fragmentOptions.MaxChunkSize;
    }

    private async Task RECEIVE_LOOP_ASYNC(CancellationToken cancellationToken = default)
    {
        WebSocketConnection owner = this;
        WebSocket webSocket = _webSocket;
        NetworkWebSocketOptions options = _wsOptions;

        // Allocate a buffer matching FragmentOptions.MaxChunkSize (typically 1.4KB) to eliminate 64KB per-connection bloat.
        int length = GET_RECEIVE_BUFFER_SIZE();
        byte[] buffer = BufferLease.ByteArrayPool.Rent(length);

        try
        {
            while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open && !owner.IsDisposed)
            {
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, length), cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                owner.AddBytesReceived(result.Count);
                owner.UpdateLastPingTime();

                if (result.EndOfMessage)
                {
                    // Fast path: the entire message fit in our buffer
                    if (result.Count > options.MaxMessageSize)
                    {
                        Throw.WebSocketMessageTooLarge();
                    }

                    if (result.Count == 0)
                    {
                        continue;
                    }

                    // Rent a lease and copy data so the receive loop can continue immediately
                    BufferLease lease = BufferLease.CopyFrom(new ReadOnlySpan<byte>(buffer, 0, result.Count));
                    lease.IsReliable = true;

                    owner.TriggerProcessEvent(lease);
                }
                else
                {
                    // Slow path: the message is larger than the buffer, we need to assemble it
                    await HANDLE_LARGE_MESSAGE_ASYNC(owner, webSocket, options, buffer, result.Count, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (WebSocketException)
        {
            // Disconnected
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (false)
            {
                // ex, "[NW.WebSocketConnection] Receive loop error");
            }
        }
        finally
        {
            BufferLease.ByteArrayPool.Return(buffer);
            owner.Disconnect("Receive loop exited");
        }
    }

    private static async Task HANDLE_LARGE_MESSAGE_ASYNC(
        WebSocketConnection owner, WebSocket webSocket, NetworkWebSocketOptions options, byte[] initialBuffer, int initialBytes, CancellationToken cancellationToken)
    {
        if (initialBytes > options.MaxMessageSize)
        {
            Throw.WebSocketMessageTooLarge();
        }

        // DO NOT rent the full MaxMessageSize upfront to prevent memory bloat/exhaustion under concurrent load.
        // Instead, we start with a small, conservative buffer size (at least 4KB or the initial chunk size)
        // and dynamically grow the buffer by doubling as needed up to MaxMessageSize.
        int initialCapacity = Math.Max(4096, initialBytes);
        if (initialCapacity > options.MaxMessageSize)
        {
            initialCapacity = options.MaxMessageSize;
        }

        byte[] payload = BufferLease.ByteArrayPool.Rent(initialCapacity);
        int written = initialBytes;
        initialBuffer.AsSpan(0, initialBytes).CopyTo(payload);

        int length = GET_RECEIVE_BUFFER_SIZE();
        byte[] buffer = BufferLease.ByteArrayPool.Rent(length);
        bool ownershipTransferred = false;
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                int nextWritten = checked(written + result.Count);

                if (nextWritten > options.MaxMessageSize)
                {
                    Throw.WebSocketMessageTooLarge();
                }

                if (nextWritten > payload.Length)
                {
                    int newCapacity = checked(payload.Length * 2);
                    if (newCapacity < nextWritten)
                    {
                        newCapacity = nextWritten;
                    }
                    if (newCapacity > options.MaxMessageSize)
                    {
                        newCapacity = options.MaxMessageSize;
                    }

                    byte[] newPayload = BufferLease.ByteArrayPool.Rent(newCapacity);
                    payload.AsSpan(0, written).CopyTo(newPayload);
                    BufferLease.ByteArrayPool.Return(payload);
                    payload = newPayload;
                }

                buffer.AsSpan(0, result.Count).CopyTo(payload.AsSpan(written));
                written = nextWritten;
                owner.AddBytesReceived(result.Count);

            } while (!result.EndOfMessage);

            if (written == 0)
            {
                return;
            }

            BufferLease lease = BufferLease.TakeOwnership(payload, 0, written);
            lease.IsReliable = true;

            owner.TriggerProcessEvent(lease);

            ownershipTransferred = true;
        }
        finally
        {
            BufferLease.ByteArrayPool.Return(buffer);

            if (!ownershipTransferred)
            {
                BufferLease.ByteArrayPool.Return(payload);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OBSERVE_RECEIVE_LOOP_SHUTDOWN(Task receiveLoopTask)
    {
        if (receiveLoopTask.IsCompleted)
        {
            if (receiveLoopTask.Exception?.GetBaseException() is Exception ex)
            {
                LOG_RECEIVE_LOOP_FAULT(ex, "during-dispose");
            }
            return;
        }

        _ = receiveLoopTask.ContinueWith(static (task, state) =>
        {
            if (task.Exception?.GetBaseException() is Exception ex)
            {
                LOG_RECEIVE_LOOP_FAULT(ex, "after-dispose");
            }
        }, this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void LOG_RECEIVE_LOOP_FAULT(Exception ex, string phase)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.WebSocketConnection:Internal", $"receive-loop-faulted-{phase} phase={phase}", ex));
        }
    }

    #endregion Receive Loop

    #region Transport Disposal

    /// <summary>
    /// Stops the receive loop and closes the WebSocket. Runs once, when the connection is disposed. After this the
    /// connection reports <see cref="IsDisposed"/>, so every send becomes a no-op.
    /// </summary>
    private void DisposeTransport()
    {
        if (Interlocked.CompareExchange(ref _transportDisposed, 1, 0) != 0)
        {
            return;
        }

        Task? receiveLoopTask = Interlocked.Exchange(ref _receiveLoopTask, null);
        if (receiveLoopTask is not null)
        {
            this.OBSERVE_RECEIVE_LOOP_SHUTDOWN(receiveLoopTask);
        }

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                _webSocket.Abort();
            }
            _webSocket.Dispose();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
    }

    #endregion Transport Disposal
}
