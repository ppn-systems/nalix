// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
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
using Nalix.Network.Connections;
using Nalix.Network.Options;

#pragma warning disable CA2213 // Disposable fields should be disposed

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Nalix.Network.Tests")]

namespace Nalix.Network.Internal.Transport;

/// <summary>
/// Adapter class that implements <see cref="IConnection.ITransport"/> for WebSocket.
/// </summary>
[SkipLocalsInit]
[DebuggerNonUserCode]
[EditorBrowsable(EditorBrowsableState.Never)]
internal sealed class WebSocketTransport : IConnection.ITransport, IPoolable, IDisposable
{
    #region Fields

    private WebSocketConnection _owner = null!;
    private NetworkWebSocketOptions _options = null!;

    private int _disposed;
    private int _receiveStarted;
    private Task? _receiveLoopTask;

    private ISequenceCounter _sendSequence = null!;
    private ISequenceCounter _receiveSequence = null!;

    private WebSocket _webSocket = null!;
    private SemaphoreSlim _sendLock = null!;
#pragma warning disable CA2213 // Scoped wrapper over _sendLock; does not own standalone unmanaged resources
    private SendLockScope _sendLockScope = null!;
#pragma warning restore CA2213

    internal WebSocket WebSocket => _webSocket;
    internal SemaphoreSlim SendLock => _sendLock;

    #endregion Fields

    #region Constructor

    public WebSocketTransport()
    {
    }

    public void Initialize(WebSocketConnection owner, WebSocket webSocket)
    {
        _sendSequence = new SequenceCounter();
        _receiveSequence = new SequenceCounter();
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _options = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        _sendLock = new SemaphoreSlim(1, 1);
        _sendLockScope = new SendLockScope(_sendLock);
        _disposed = 0;
        _receiveStarted = 0;
        _receiveLoopTask = null;
    }

    public void ResetForPool()
    {
        _owner = null!;
        _webSocket = null!;
        _options = null!;
        _sendSequence = null!;
        _receiveSequence = null!;
        _disposed = 0;
        _receiveStarted = 0;
        _receiveLoopTask = null;

        try { _sendLock?.Dispose(); }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }

        _sendLockScope = null!;
        _sendLock = null!;
    }

    #endregion Constructor

    #region Properties

    /// <inheritdoc/>
    public TransportFraming Framing => TransportFraming.None;

    /// <inheritdoc/>
    public ISequenceCounter SendSequence => _sendSequence;

    /// <inheritdoc/>
    public ISequenceCounter ReceiveSequence => _receiveSequence;

    /// <inheritdoc/>
    public uint NextSendSequence() => _sendSequence.Next();

    /// <inheritdoc/>
    public uint NextReceiveSequence() => _receiveSequence.Next();

    /// <inheritdoc/>
    public uint CurrentSendSequence => _sendSequence.Current();

    /// <inheritdoc/>
    public uint CurrentReceiveSequence => _receiveSequence.Current();

    #endregion Properties

    #region APIs

    [StackTraceHidden]
    public void Send(ReadOnlySpan<byte> message) => Throw.WebSocketSyncSendNotSupported();

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        // Peer already closed/closing -- swallow here (same as SocketTcpTransport's
        // PeerClosed/Aborted swallow) instead of throwing, so callers several layers up
        // (dispatch/control-send paths) don't have to catch-and-classify this by exception
        // message string.
        if (_owner.IsDisposed || _webSocket.State != WebSocketState.Open)
        {
            return default;
        }

        // WebSockets handle framing natively, so we just send the message as binary.
        // A SemaphoreSlim is used because WebSocket.SendAsync doesn't support concurrent calls.
        if (!cancellationToken.IsCancellationRequested && _sendLock.Wait(0, cancellationToken))
        {
            ValueTask vt;
            try
            {
                vt = this.SEND_CORE_ASYNC(message, cancellationToken);
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

        static async ValueTask AWAIT_ACQUIRE_AND_SEND_ASYNC(WebSocketTransport transport, ReadOnlyMemory<byte> msg, CancellationToken ct)
        {
            await transport._sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await transport.SEND_CORE_ASYNC(msg, ct).ConfigureAwait(false);
            }
            finally
            {
                _ = transport._sendLock.Release();
            }
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ValueTask<IAsyncDisposable> IConnection.ITransport.AcquireSendLockAsync(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested && _sendLock.Wait(0, cancellationToken))
        {
            return new ValueTask<IAsyncDisposable>(_sendLockScope);
        }

        return this.ACQUIRE_SEND_LOCK_ASYNC(cancellationToken);
    }

    private async ValueTask<IAsyncDisposable> ACQUIRE_SEND_LOCK_ASYNC(CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _sendLockScope;
    }

    /// <summary>
    /// Releases a <see cref="WebSocketTransport"/> send lock previously acquired via
    /// <see cref="ACQUIRE_SEND_LOCK_ASYNC"/>.
    /// </summary>
    private sealed class SendLockScope(SemaphoreSlim sendLock) : IAsyncDisposable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask DisposeAsync()
        {
            _ = sendLock.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    ValueTask IConnection.ITransport.SendAsyncCore(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (_owner.IsDisposed || _webSocket.State != WebSocketState.Open)
        {
            return ValueTask.CompletedTask;
        }

        return this.SEND_CORE_ASYNC(message, cancellationToken);
    }

    private async ValueTask SEND_CORE_ASYNC(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        await _webSocket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);

        _owner.AddBytesSent(message.Length);
        _owner.TriggerPostProcessEvent();
    }

    [StackTraceHidden]
    public void BeginReceive(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, nameof(WebSocketConnection));

        if (Interlocked.CompareExchange(ref _receiveStarted, 1, 0) != 0)
        {
            return;
        }

        _receiveLoopTask = this.RECEIVE_LOOP_ASYNC(cancellationToken);
    }

    /// <inheritdoc/>
    public void UseFraming(TransportFraming framing)
    {
        // TODO: Review framing behavior. WebSocket currently relies on its built-in message framing.
    }

    #endregion APIs

    #region Receive Loop

    private static readonly FragmentOptions s_fragmentOptions = ConfigurationManager.Instance.Get<FragmentOptions>();

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
        // Allocate a buffer matching FragmentOptions.MaxChunkSize (typically 1.4KB) to eliminate 64KB per-connection bloat.
        int length = GET_RECEIVE_BUFFER_SIZE();
        byte[] buffer = BufferLease.ByteArrayPool.Rent(length);

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open && !_owner.IsDisposed)
            {
                WebSocketReceiveResult result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, length), cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                _owner.AddBytesReceived(result.Count);
                _owner.UpdateLastPingTime();

                if (result.EndOfMessage)
                {
                    // Fast path: the entire message fit in our buffer
                    if (result.Count > _options.MaxMessageSize)
                    {
                        Throw.WebSocketMessageTooLarge();
                    }

                    if (result.Count == 0)
                    {
                        return;
                    }

                    // Rent a lease and copy data so the receive loop can continue immediately
                    BufferLease lease = BufferLease.CopyFrom(new ReadOnlySpan<byte>(buffer, 0, result.Count));
                    lease.IsReliable = true;

                    _owner.TriggerProcessEvent(lease);
                }
                else
                {
                    // Slow path: the message is larger than the buffer, we need to assemble it
                    await this.HANDLE_LARGE_MESSAGE_ASYNC(buffer, result.Count, cancellationToken).ConfigureAwait(false);
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
            _owner.Disconnect("Receive loop exited");
        }
    }

    private async Task HANDLE_LARGE_MESSAGE_ASYNC(byte[] initialBuffer, int initialBytes, CancellationToken cancellationToken)
    {
        if (initialBytes > _options.MaxMessageSize)
        {
            Throw.WebSocketMessageTooLarge();
        }

        // DO NOT rent the full MaxMessageSize upfront to prevent memory bloat/exhaustion under concurrent load.
        // Instead, we start with a small, conservative buffer size (at least 4KB or the initial chunk size)
        // and dynamically grow the buffer by doubling as needed up to MaxMessageSize.
        int initialCapacity = Math.Max(4096, initialBytes);
        if (initialCapacity > _options.MaxMessageSize)
        {
            initialCapacity = _options.MaxMessageSize;
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
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                int nextWritten = checked(written + result.Count);

                if (nextWritten > _options.MaxMessageSize)
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
                    if (newCapacity > _options.MaxMessageSize)
                    {
                        newCapacity = _options.MaxMessageSize;
                    }

                    byte[] newPayload = BufferLease.ByteArrayPool.Rent(newCapacity);
                    payload.AsSpan(0, written).CopyTo(newPayload);
                    BufferLease.ByteArrayPool.Return(payload);
                    payload = newPayload;
                }

                buffer.AsSpan(0, result.Count).CopyTo(payload.AsSpan(written));
                written = nextWritten;
                _owner.AddBytesReceived(result.Count);

            } while (!result.EndOfMessage);

            if (written == 0)
            {
                BufferLease.ByteArrayPool.Return(buffer);
                return;
            }

            BufferLease lease = BufferLease.TakeOwnership(payload, 0, written);
            lease.IsReliable = true;

            _owner.TriggerProcessEvent(lease);

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
        }, _owner, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void LOG_RECEIVE_LOOP_FAULT(Exception ex, string phase)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.WebSocketTransport:Internal", $"receive-loop-faulted-{phase} phase={phase}", ex));
        }
    }

    #endregion Receive Loop

    #region Dispose

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
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
            if (_webSocket is not null)
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    _webSocket.Abort();
                }
                _webSocket.Dispose();
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
    }

    #endregion Dispose
}
