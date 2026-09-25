// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Transforms;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.SDK.Options;

namespace Nalix.SDK.Transport.Internal.Udp;

/// <summary>
/// Handles sending UDP datagrams with session token prefix.
/// UDP does NOT use framing or fragmentation (as per requirement).
/// </summary>
internal sealed class UdpFrameSender : IDisposable
{
    private readonly SessionState _state;
    private readonly Func<Socket> _getSocket;
    private readonly SequenceCounter _sequence;
    private readonly TransportOptions _options;
    private readonly Action<Exception, Socket?> _onError;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    internal SequenceCounter Sequence => _sequence;

    private int _disposed;

    public UdpFrameSender(Func<Socket> getSocket, TransportOptions options, SessionState state, Action<Exception, Socket?> onError)
    {
        _sequence = new SequenceCounter();
        _getSocket = getSocket ?? throw new ArgumentNullException(nameof(getSocket));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    /// <summary>
    /// Send raw payload (already processed by FramePipeline).
    /// </summary>
    public async Task<bool> SendAsync(ReadOnlyMemory<byte> payload, bool? encryptOverride, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, nameof(UdpFrameSender));

        BufferLease src = BufferLease.CopyFrom(payload.Span);
        IBufferLease current = src;

        try
        {
            bool encrypt = encryptOverride ?? _state.EncryptionEnabled;

            // [SECURITY] src is the plaintext of an encrypted datagram: scrub it on release even if
            // the send throws before FramePipeline.ProcessOutbound marks it.
            src.ZeroOnDispose = encrypt;
            if (encrypt && _sequence.IsApproachingOverflow())
            {
                throw new CipherException(
                    "Send sequence counter is approaching overflow. Key rotation is required before it wraps.");
            }

            uint? seqToUse = encrypt ? _sequence.Next() : null;

            // Transform outbound: Compress -> Encrypt + Sequence
            FramePipeline.ProcessOutbound(
                ref current,
                _options.CompressionEnabled,
                _options.CompressionThreshold,
                encryptOverride ?? _state.EncryptionEnabled,
                _state.Secret.AsSpan(),
                seqToUse,
                _options.Algorithm);

            if (current.Length + ISnowflake.Size > _options.MaxUdpDatagramSize)
            {
                throw new NetworkException(
                    $"UDP datagram too large after transformation: {current.Length + ISnowflake.Size} bytes. Max = {_options.MaxUdpDatagramSize}");
            }

            // Envelope: [SessionToken (8 bytes) | Transformed Payload | XxHash32 Tag (4 bytes)]
            int dataLen = ISnowflake.Size + current.Length;
            using BufferLease finalLease = BufferLease.Rent(dataLen + Math.Max(4, Bytes32.Size));

            // 1. Write SessionToken
            BinaryPrimitives.WriteUInt64LittleEndian(finalLease.SpanFull[..ISnowflake.Size], _state.SessionToken);

            // 2. Write Transformed Payload
            current.Span.CopyTo(finalLease.SpanFull[ISnowflake.Size..]);

            // 3. Temporarily append Secret for hashing
            _state.Secret.AsSpan().CopyTo(finalLease.SpanFull[dataLen..]);

            // 4. Compute XxHash32
            uint hash = XxHash32.Compute(finalLease.SpanFull[..(dataLen + Bytes32.Size)]);

            // 5. Overwrite the start of Secret with the 4-byte Tag
            BinaryPrimitives.WriteUInt32LittleEndian(finalLease.SpanFull.Slice(dataLen, 4), hash);

            // [SECURITY] Scrub the rest of the secret: it sits past the committed length and the
            // pool does not clear arrays on return.
            finalLease.SpanFull.Slice(dataLen + 4, Bytes32.Size - 4).Clear();

            // 6. Commit the final datagram length (Token + Payload + Tag)
            finalLease.CommitLength(dataLen + 4);

            return await this.SendRawAsync(finalLease.Memory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _onError(ex, _getSocket());
            return false;
        }
        finally
        {
            if (!ReferenceEquals(current, src))
            {
                current.Dispose();
            }
            src.Dispose();
        }
    }

    private async Task<bool> SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Socket? socket = _getSocket() ?? throw new NetworkException("Cannot send: UDP socket is null.");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            _ = await socket.SendAsync(data, SocketFlags.None, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _onError(ex, socket);
            return false;
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _sendLock.Dispose();
    }
}
