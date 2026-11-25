// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// once per connection and never pooled, so a caller that kept <see cref="Transport"/> cannot reach a later connection.
/// Its sequence counters are pooled with the connection's backing; after the connection is disposed every send is a
/// no-op.
/// </summary>
public sealed partial class WebSocketConnection : IConnection.ITransport
{
    #region Transport Fields

    /// <summary>Replaces the counters of a closed connection so stale callers never touch the next owner's counters.</summary>
    private static readonly SequenceCounter s_detachedSequence = new();

    private static readonly FragmentOptions s_fragmentOptions = ConfigurationManager.Instance.Get<FragmentOptions>();

    /// <summary>
    /// Upper bound for the closing handshake: waiting for the send lock, writing the close frame and waiting for the
    /// peer's close frame. A peer that does not answer in time is dropped.
    /// </summary>
    private static readonly TimeSpan s_closeHandshakeTimeout = TimeSpan.FromSeconds(2);

    // Close reasons stay well below the 123-byte limit RFC 6455 puts on a close frame's reason.
    private const string CloseReasonNormal = "Connection closed";
    private const string CloseReasonMessageTooBig = "Message exceeds the maximum size";

    private WebSocket _webSocket;
    private CoalescingWriteStream? _writeBatcher;
    private NetworkWebSocketOptions _wsOptions;

    // The scope handed out by AcquireSendLockAsync. A separate object, not the connection itself: if the connection
    // implemented IAsyncDisposable, `await using` (or any disposer that prefers DisposeAsync) would release the lock
    // instead of closing the connection. One per connection, as master allocated one per Initialize.
#pragma warning disable CA2213 // Holds only a SemaphoreSlim without AvailableWaitHandle: no unmanaged resource.
    private readonly SendLockScope _sendLockScope = new();
#pragma warning restore CA2213

    private int _transportDisposed;
    private int _receiveStarted;
    private Task? _receiveLoopTask;

    // The close status sent when the connection closes; 0 until a caller picks one (NormalClosure is the default).
    private int _closeStatus;
    private string? _closeReason;
    private Task _closeTask = Task.CompletedTask;

    #endregion Transport Fields

    #region Transport Helpers

    private SequenceCounter WsSendSequence => Volatile.Read(ref _backing)?.SendSequence ?? s_detachedSequence;

    private SequenceCounter WsReceiveSequence => Volatile.Read(ref _backing)?.ReceiveSequence ?? s_detachedSequence;

    /// <summary>Gets the underlying WebSocket of this connection.</summary>
    internal WebSocket WebSocket => _webSocket;

    /// <summary>Gets the lock that serializes sends on the WebSocket.</summary>
    internal SemaphoreSlim SendLock => _sendLockScope.Lock;

    /// <summary>
    /// Wires the WebSocket into this connection. Called once from the constructor.
    /// </summary>
    [MemberNotNull(nameof(_webSocket), nameof(_wsOptions))]
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
        if (!cancellationToken.IsCancellationRequested && this.SendLock.Wait(0, cancellationToken))
        {
            ValueTask vt;
            try
            {
                vt = this.SEND_CORE_ASYNC(message, cancellationToken);
            }
            catch
            {
                _ = this.SendLock.Release();
                throw;
            }

            if (vt.IsCompletedSuccessfully)
            {
                _ = this.SendLock.Release();
                this.TriggerPostProcessEvent();
                return default;
            }

            return AWAIT_WITH_RELEASE_ASYNC(this, vt);
        }

        return AWAIT_ACQUIRE_AND_SEND_ASYNC(this, message, cancellationToken);

        // The post-send event runs after the lock is released, so a handler never runs under the write lock.
        static async ValueTask AWAIT_WITH_RELEASE_ASYNC(WebSocketConnection connection, ValueTask vt)
        {
            try
            {
                await vt.ConfigureAwait(false);
            }
            finally
            {
                _ = connection.SendLock.Release();
            }

            connection.TriggerPostProcessEvent();
        }

        static async ValueTask AWAIT_ACQUIRE_AND_SEND_ASYNC(WebSocketConnection connection, ReadOnlyMemory<byte> msg, CancellationToken ct)
        {
            await connection.SendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.SEND_CORE_ASYNC(msg, ct).ConfigureAwait(false);
            }
            finally
            {
                _ = connection.SendLock.Release();
            }

            connection.TriggerPostProcessEvent();
        }
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
    [StackTraceHidden]
    ValueTask IConnection.ITransport.SendAsyncCore(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (this.IsDisposed || _webSocket.State != WebSocketState.Open)
        {
            return ValueTask.CompletedTask;
        }

        return this.SEND_AND_NOTIFY_ASYNC(message, cancellationToken);
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

                await this.SEND_AND_NOTIFY_ASYNC(frames.Span[i].Memory, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Writes one WebSocket message and counts its bytes. A write that fails because this connection was closed while it was
    /// in flight is a no-op, as a send after close is; any other failure propagates.
    /// </summary>
    private async ValueTask SEND_CORE_ASYNC(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        try
        {
            await _webSocket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex) && this.IsDisposed)
        {
            return;
        }

        this.AddBytesSent(message.Length);
    }

    /// <summary>
    /// Writes one message for <see cref="IConnection.ITransport.SendAsyncCore(ReadOnlyMemory{byte}, CancellationToken)"/>,
    /// then raises the post-send event. The caller may hold the send lock here (the packet pipeline does), so the event
    /// only queues its handlers; it must not do blocking work inline.
    /// </summary>
    private async ValueTask SEND_AND_NOTIFY_ASYNC(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        await this.SEND_CORE_ASYNC(message, cancellationToken).ConfigureAwait(false);
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
        WebSocketCloseStatus? violation = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open && !owner.IsDisposed)
            {
                // The Memory overload: the ArraySegment one wraps every result in a new Task and WebSocketReceiveResult.
                ValueWebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

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
                        violation = WebSocketCloseStatus.MessageTooBig;
                        break;
                    }

                    if (result.Count == 0)
                    {
                        continue;
                    }

                    // Rent a lease and copy data so the receive loop can continue immediately
                    BufferLease lease = BufferLease.CopyFrom(new ReadOnlySpan<byte>(buffer, 0, result.Count));

                    owner.TriggerProcessEvent(lease);
                }
                else
                {
                    // Slow path: the message is larger than the buffer, we need to assemble it
                    violation = await HANDLE_LARGE_MESSAGE_ASYNC(owner, webSocket, options, buffer, result.Count, cancellationToken).ConfigureAwait(false);
                    if (violation is not null)
                    {
                        break;
                    }
                }
            }

            if (violation is WebSocketCloseStatus status)
            {
                await owner.CLOSE_FOR_VIOLATION_ASYNC(webSocket, status, buffer, length).ConfigureAwait(false);
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

    /// <summary>
    /// Assembles a message that spans several reads and hands it to the connection. Returns the close status to end the
    /// connection with when the message breaks a limit, or <see langword="null"/> otherwise.
    /// </summary>
    private static async Task<WebSocketCloseStatus?> HANDLE_LARGE_MESSAGE_ASYNC(
        WebSocketConnection owner, WebSocket webSocket, NetworkWebSocketOptions options, byte[] initialBuffer, int initialBytes, CancellationToken cancellationToken)
    {
        if (initialBytes > options.MaxMessageSize)
        {
            return WebSocketCloseStatus.MessageTooBig;
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
            ValueWebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                int nextWritten = checked(written + result.Count);

                if (nextWritten > options.MaxMessageSize)
                {
                    return WebSocketCloseStatus.MessageTooBig;
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
                return null;
            }

            BufferLease lease = BufferLease.TakeOwnership(payload, 0, written);

            owner.TriggerProcessEvent(lease);

            ownershipTransferred = true;
            return null;
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

    /// <summary>
    /// Ends the connection after the peer broke a limit: sends a close frame with <paramref name="status"/>, then
    /// reads (and discards) until the peer's close frame arrives, so unread bytes do not turn the close into a reset.
    /// </summary>
    private async Task CLOSE_FOR_VIOLATION_ASYNC(WebSocket webSocket, WebSocketCloseStatus status, byte[] buffer, int length)
    {
        const string reason = CloseReasonMessageTooBig;
        this.SetCloseStatus(status, reason);

        using CancellationTokenSource cts = new(s_closeHandshakeTimeout);

        if (!await this.SEND_CLOSE_FRAME_ASYNC(webSocket, status, reason, cts.Token).ConfigureAwait(false))
        {
            return;
        }

        await DRAIN_UNTIL_PEER_CLOSE_ASYNC(webSocket, buffer, length, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// After this side's close frame is out, reads and discards whatever the peer still sends until its close frame
    /// arrives. Frames left unread when the socket is released make the kernel answer with a TCP reset, and the peer
    /// then sees a broken pipe or 1006 instead of the close status it was sent.
    /// </summary>
    private static async Task DRAIN_UNTIL_PEER_CLOSE_ASYNC(WebSocket webSocket, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        while (webSocket.State == WebSocketState.CloseSent)
        {
            ValueWebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Writes a close frame under the send lock, so it never interleaves with (or is held back by) a batch of messages.
    /// Returns <see langword="false"/> when the WebSocket can no longer send one.
    /// </summary>
    private async Task<bool> SEND_CLOSE_FRAME_ASYNC(WebSocket webSocket, WebSocketCloseStatus status, string reason, CancellationToken cancellationToken)
    {
        await this.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return false;
            }

            await webSocket.CloseOutputAsync(status, reason, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _ = this.SendLock.Release();
        }
    }

    /// <summary>
    /// Picks the close status sent when this connection closes. The first caller wins.
    /// </summary>
    private void SetCloseStatus(WebSocketCloseStatus status, string reason)
    {
        if (Interlocked.CompareExchange(ref _closeStatus, (int)status, 0) == 0)
        {
            Volatile.Write(ref _closeReason, reason);
        }
    }

    /// <summary>
    /// Closes the connection with a WebSocket close frame carrying <paramref name="status"/>. The returned task
    /// completes when the closing handshake is over (or timed out) and the socket is released.
    /// </summary>
    /// <param name="status">The RFC 6455 close status to send.</param>
    /// <param name="reason">A short reason, at most 123 UTF-8 bytes.</param>
    internal Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        this.SetCloseStatus(status, reason);
        this.Disconnect(reason);
        return Volatile.Read(ref _closeTask);
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

        // A WebSocket that can still send ends with a close frame (RFC 6455 section 7.1.2), not a bare TCP drop.
        if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            int status = Volatile.Read(ref _closeStatus);
            Volatile.Write(ref _closeTask, this.CLOSE_GRACEFULLY_ASYNC(
                _webSocket,
                status == 0 ? WebSocketCloseStatus.NormalClosure : (WebSocketCloseStatus)status,
                Volatile.Read(ref _closeReason) ?? CloseReasonNormal,
                receiveLoopTask));
            return;
        }

        RELEASE_WEBSOCKET(_webSocket);
    }

    /// <summary>
    /// Runs the closing handshake: sends the close frame, reads until the peer's close frame arrives, then releases the
    /// socket. Bounded by <see cref="s_closeHandshakeTimeout"/>.
    /// </summary>
    private async Task CLOSE_GRACEFULLY_ASYNC(WebSocket webSocket, WebSocketCloseStatus status, string reason, Task? receiveLoopTask)
    {
        try
        {
            using CancellationTokenSource cts = new(s_closeHandshakeTimeout);

            if (await this.SEND_CLOSE_FRAME_ASYNC(webSocket, status, reason, cts.Token).ConfigureAwait(false) &&
                webSocket.State == WebSocketState.CloseSent)
            {
                // The receive loop stops at its next frame (this connection is disposed and no longer Open), not at the
                // peer's close frame. Wait for it so only one read runs at a time, then read the rest here.
                if (receiveLoopTask is { IsCompleted: false })
                {
                    await receiveLoopTask.WaitAsync(cts.Token).ConfigureAwait(false);
                }

                byte[] buffer = BufferLease.ByteArrayPool.Rent(GET_RECEIVE_BUFFER_SIZE());
                try
                {
                    await DRAIN_UNTIL_PEER_CLOSE_ASYNC(webSocket, buffer, buffer.Length, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    BufferLease.ByteArrayPool.Return(buffer);
                }
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
        }
        finally
        {
            RELEASE_WEBSOCKET(webSocket);
        }
    }

    private static void RELEASE_WEBSOCKET(WebSocket webSocket)
    {
        try
        {
            if (webSocket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
            {
                webSocket.Abort();
            }

            webSocket.Dispose();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
    }

    #endregion Transport Disposal
}
