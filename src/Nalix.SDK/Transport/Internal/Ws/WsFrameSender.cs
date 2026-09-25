// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Transforms;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.SDK.Options;

namespace Nalix.SDK.Transport.Internal.Ws;

internal sealed class WsFrameSender : IDisposable
{
    private readonly SessionState _state;
    private readonly SemaphoreSlim _sendLock;
    private readonly SequenceCounter _sequence;
    internal SequenceCounter Sequence => _sequence;
    private readonly TransportOptions _options;
    private readonly Action<Exception, ClientWebSocket?> _onError;
    private readonly Func<ClientWebSocket> _getSocket;

    private int _disposed;

    public WsFrameSender(Func<ClientWebSocket> getSocket, TransportOptions options, SessionState state, Action<Exception, ClientWebSocket?> onError)
    {
        _sendLock = new(1, 1);
        _sequence = new SequenceCounter();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _getSocket = getSocket ?? throw new ArgumentNullException(nameof(getSocket));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    #region Public Send APIs

    public bool Send(ReadOnlySpan<byte> data, bool encrypt = true)
        => this.Send(BufferLease.CopyFrom(data), encrypt);

    public bool Send(IBufferLease lease, bool encrypt = true)
        => this.SEND_CORE(lease, encrypt, sync: true, default).GetAwaiter().GetResult();

    public Task<bool> SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
        => this.SendAsync(BufferLease.CopyFrom(payload.Span), encrypt, ct);

    public Task<bool> SendAsync(IBufferLease lease, bool? encrypt = null, CancellationToken ct = default)
        => this.SEND_CORE(lease, encrypt ?? _state.EncryptionEnabled, sync: false, ct);

    #endregion

    #region Core Implementation

    private async Task<bool> SEND_CORE(IBufferLease lease, bool encrypt, bool sync, CancellationToken ct)
    {
        IBufferLease current = lease;

        // [SECURITY] The caller's lease is the plaintext of an encrypted frame. The pool does not
        // clear arrays on return, so mark it for scrubbing before anything can throw.
        if (encrypt && lease is BufferLease plaintextLease)
        {
            plaintextLease.ZeroOnDispose = true;
        }

        try
        {
            if (encrypt && _sequence.IsApproachingOverflow())
            {
                throw new CipherException(
                    "Send sequence counter is approaching overflow. Key rotation is required before it wraps.");
            }

            uint? seqToUse = encrypt ? _sequence.Next() : null;

            FramePipeline.ProcessOutbound(
                ref current,
                _options.CompressionEnabled,
                _options.CompressionThreshold,
                encrypt,
                _state.Secret.AsSpan(),
                seqToUse,
                _options.Algorithm);

            return sync
                ? this.SEND_RAW(current.Memory)
                : await this.SEND_RAW_ASYNC(current.Memory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _onError?.Invoke(ex, _getSocket());
            return false;
        }
        finally
        {
            if (!ReferenceEquals(current, lease))
            {
                current.Dispose();
            }

            lease.Dispose();
        }
    }

    private bool SEND_RAW(ReadOnlyMemory<byte> frame)
    {
        ClientWebSocket socket = _getSocket();
        if (socket.State != WebSocketState.Open)
        {
            return false;
        }

        _sendLock.Wait();
        try
        {
            socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _onError?.Invoke(ex, socket);
            return false;
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }

    private async Task<bool> SEND_RAW_ASYNC(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        ClientWebSocket socket = _getSocket();
        if (socket.State != WebSocketState.Open)
        {
            return false;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _onError?.Invoke(ex, socket);
            return false;
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }

    #endregion

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _sendLock.Dispose();
    }
}
