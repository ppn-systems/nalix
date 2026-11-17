// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Diagnostics;
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
#pragma warning disable CA2213 // _sendLock never allocates an OS WaitHandle and must stay undisposed so in-flight WaitAsync waiters can drain on teardown.
    private readonly SemaphoreSlim _sendLock;
#pragma warning restore CA2213
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

    /// <summary>
    /// Sends <paramref name="lease"/> and runs <paramref name="afterWrite"/> while still holding the send lock, so no
    /// other frame is encrypted or written between the two. Used by rekey to switch the key right after the
    /// SESSION_REKEY frame, before any later frame is encrypted.
    /// </summary>
    internal Task<bool> SendThenAsync(IBufferLease lease, bool encrypt, Action afterWrite, CancellationToken ct)
        => this.SEND_CORE(lease, encrypt, sync: false, ct, afterWrite);

    #endregion

    #region Core Implementation

    private async Task<bool> SEND_CORE(IBufferLease lease, bool encrypt, bool sync, CancellationToken ct, Action? afterWrite = null)
    {
        IBufferLease current = lease;

        // Captured once, before anything below can throw. A framing/encryption failure below has
        // nothing to do with which physical socket is live by the time the catch runs — re-reading
        // _getSocket() there would report whatever the field points to NOW, which a fast reconnect
        // racing this call can have already moved to a newer connection's socket. Reporting the
        // socket THIS call actually started with keeps HandleError's identity check meaningful.
        ClientWebSocket originatingSocket = _getSocket();

        // [SECURITY] The caller's lease is the plaintext of an encrypted frame. The pool does not
        // clear arrays on return, so mark it for scrubbing before anything can throw.
        if (encrypt && lease is BufferLease plaintextLease)
        {
            plaintextLease.ZeroOnDispose = true;
        }

        bool lockTaken = false;
        try
        {
            // The send lock covers encryption as well as the write: frames reach the wire in the order of their
            // sequence numbers and of the key they were encrypted with, so a key switch (rekey) can never let an
            // older-key frame follow the switch.
            if (sync)
            {
#pragma warning disable CA1849 // The synchronous Send() API takes this path; it must not await.
                _sendLock.Wait(ct);
#pragma warning restore CA1849
            }
            else
            {
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            }

            lockTaken = true;

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

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Frames.Sent) && current.Length >= Nalix.Abstractions.Networking.Packets.PacketConstants.HeaderSize)
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Frames.Sent, new DiagnosticLog("SDK.WsFrameSender:Send", $"sent transport=websocket opcode=0x{System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(current.Span):X4} length={current.Length} encrypted={encrypt}"));
            }

            bool sent = sync
                ? this.SEND_RAW(current.Memory, originatingSocket)
                : await this.SEND_RAW_ASYNC(current.Memory, originatingSocket, ct).ConfigureAwait(false);

            if (sent)
            {
                afterWrite?.Invoke();
            }

            return sent;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _onError?.Invoke(ex, originatingSocket);
            return false;
        }
        finally
        {
            if (lockTaken)
            {
                _ = _sendLock.Release();
            }

            if (!ReferenceEquals(current, lease))
            {
                current.Dispose();
            }

            lease.Dispose();
        }
    }

    // The caller (SEND_CORE) holds the send lock.
    private bool SEND_RAW(ReadOnlyMemory<byte> frame, ClientWebSocket socket)
    {
        if (Volatile.Read(ref _disposed) != 0 || socket.State != WebSocketState.Open)
        {
            return false;
        }

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
    }

    // The caller (SEND_CORE) holds the send lock.
    private async Task<bool> SEND_RAW_ASYNC(ReadOnlyMemory<byte> frame, ClientWebSocket socket, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || socket.State != WebSocketState.Open)
        {
            return false;
        }

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
    }

    #endregion

    public void Dispose() => _ = Interlocked.CompareExchange(ref _disposed, 1, 0);
}
