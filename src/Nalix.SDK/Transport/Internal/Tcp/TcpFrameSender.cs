// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Transforms;
using Nalix.Environment.Configuration;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;
using Nalix.Environment.Sequencing;
using Nalix.SDK.Options;

namespace Nalix.SDK.Transport.Internal.Tcp;

internal sealed class TcpFrameSender : IDisposable
{
    private const int MaxTcpFrameLength = ushort.MaxValue;
    private const int HeaderSize = TcpSession.HeaderSize;

    private readonly Func<Socket> _getSocket;
    private readonly SemaphoreSlim _sendLock;
    private readonly SequenceCounter _sequence;
    internal SequenceCounter Sequence => _sequence;
    private readonly Action<Exception, Socket?> _onError;

    private readonly SessionState _state;
    private readonly TransportOptions _options;
    private readonly FragmentOptions _fragmentOptions;

    private int _disposed;

    public TcpFrameSender(
        Func<Socket> getSocket,
        TransportOptions options,
        SessionState state,
        Action<Exception, Socket?> onError)
    {
        _sendLock = new(1, 1);
        _sequence = new SequenceCounter();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _fragmentOptions = ConfigurationManager.Instance.Get<FragmentOptions>();
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

            FramePipeline.ProcessOutbound(
                ref current,
                _options.CompressionEnabled,
                _options.CompressionThreshold,
                encrypt,
                _state.Secret.AsSpan(),
                encrypt ? _sequence.Next() : null,
                _options.Algorithm);

            return current.Length >= _fragmentOptions.MaxChunkSize
                ? await this.SEND_FRAGMENTED_ASYNC(current.Memory, sync, ct).ConfigureAwait(false)
                : await this.SEND_SINGLE_FRAME_ASYNC(current.Memory, sync, ct).ConfigureAwait(false);
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

    private async Task<bool> SEND_SINGLE_FRAME_ASYNC(ReadOnlyMemory<byte> payload, bool sync, CancellationToken ct)
    {
        int totalLen = HeaderSize + payload.Length;
        if (totalLen > MaxTcpFrameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"Frame too large: {totalLen}");
        }

        byte[] frame = BufferLease.ByteArrayPool.Rent(totalLen);
        try
        {
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)totalLen);
            payload.Span.CopyTo(frame.AsSpan(HeaderSize));

            return sync
                ? this.SEND_RAW(frame, totalLen)
                : await this.SEND_RAW_ASYNC(frame, totalLen, ct).ConfigureAwait(false);
        }
        finally
        {
            BufferLease.ByteArrayPool.Return(frame);
        }
    }

    private async Task<bool> SEND_FRAGMENTED_ASYNC(ReadOnlyMemory<byte> payload, bool sync, CancellationToken ct)
    {
        if (payload.Length > _fragmentOptions.MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        ushort streamId = FragmentStreamId.Next();
        int chunkSize = _fragmentOptions.MaxChunkSize;
        int totalChunks = (payload.Length + chunkSize - 1) / chunkSize;

        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * chunkSize;
            int chunkLen = Math.Min(chunkSize, payload.Length - offset);
            bool isLast = i == totalChunks - 1;

            FragmentHeader fragHeader = new(streamId, (ushort)i, (ushort)totalChunks, isLast);

            int frameLen = HeaderSize + FragmentHeader.WireSize + chunkLen;

            byte[] frame = BufferLease.ByteArrayPool.Rent(frameLen);
            try
            {
                BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frameLen);
                fragHeader.WriteTo(frame.AsSpan(HeaderSize));
                payload.Span.Slice(offset, chunkLen).CopyTo(frame.AsSpan(HeaderSize + FragmentHeader.WireSize));

                bool sent = sync
                    ? this.SEND_RAW(frame, frameLen)
                    : await this.SEND_RAW_ASYNC(frame, frameLen, ct).ConfigureAwait(false);

                if (!sent)
                {
                    return false;
                }
            }
            finally
            {
                BufferLease.ByteArrayPool.Return(frame);
            }
        }

        return true;
    }

    #endregion

    #region Low-level Send

    private bool SEND_RAW(byte[] frame, int length)
    {
        _sendLock.Wait();
        try
        {
            return SEND_LOOP(_getSocket(), frame, length);
        }
        finally { _ = _sendLock.Release(); }
    }

    private async Task<bool> SEND_RAW_ASYNC(byte[] frame, int length, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SEND_LOOP_ASYNC(_getSocket(), frame, length, ct).ConfigureAwait(false);
        }
        finally { _ = _sendLock.Release(); }
    }

    private static bool SEND_LOOP(Socket socket, byte[] buffer, int length)
    {
        int sent = 0;
        while (sent < length)
        {
            int n = socket.Send(new ReadOnlySpan<byte>(buffer, sent, length - sent));
            if (n == 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            sent += n;
        }
        return true;
    }

    private static async Task<bool> SEND_LOOP_ASYNC(Socket socket, byte[] buffer, int length, CancellationToken ct)
    {
        int sent = 0;
        while (sent < length)
        {
            int n = await socket.SendAsync(new ReadOnlyMemory<byte>(buffer, sent, length - sent), SocketFlags.None, ct)
                                .ConfigureAwait(false);
            if (n == 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            sent += n;
        }
        return true;
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
