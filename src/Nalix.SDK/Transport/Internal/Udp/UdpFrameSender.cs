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
    private const int UdpMacTagSize = sizeof(uint);
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

        // Captured once, before anything below can throw. A framing/encryption failure below has
        // nothing to do with which physical socket is live by the time the catch runs — re-reading
        // _getSocket() there would report whatever the field points to NOW, which a fast reconnect
        // racing this call can have already moved to a newer connection's socket. Reporting the
        // socket THIS call actually started with keeps HandleError's identity check meaningful.
        Socket? originatingSocket = _getSocket();

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

            if (_state.Secret.IsZero)
            {
                throw new CipherException("Cannot send UDP datagram before session secret is established.");
            }

            // Transform outbound: Compress -> Encrypt + Sequence
            FramePipeline.ProcessOutbound(
                ref current,
                _options.CompressionEnabled,
                _options.CompressionThreshold,
                encryptOverride ?? _state.EncryptionEnabled,
                _state.Secret.AsSpan(),
                seqToUse,
                _options.Algorithm);

            if (current.Length + ISnowflake.Size + UdpMacTagSize > _options.MaxUdpDatagramSize)
            {
                throw new NetworkException(
                    $"UDP datagram too large after transformation: {current.Length + ISnowflake.Size + UdpMacTagSize} bytes. Max = {_options.MaxUdpDatagramSize}");
            }

            // Envelope: [SessionToken (8 bytes) | Transformed Payload | Keyed MAC Tag (4 bytes)]
            int dataLen = ISnowflake.Size + current.Length;
            using BufferLease finalLease = BufferLease.Rent(dataLen + UdpMacTagSize);

            // 1. Write SessionToken
            BinaryPrimitives.WriteUInt64LittleEndian(finalLease.SpanFull[..ISnowflake.Size], _state.SessionToken);

            // 2. Write Transformed Payload
            current.Span.CopyTo(finalLease.SpanFull[ISnowflake.Size..]);

            // 3. Compute seeded XxHash32 tag without copying Secret into the packet buffer
            uint payloadHash = XxHash32.Compute(finalLease.SpanFull[..dataLen]);
            uint computedTag = XxHash32.Compute(_state.Secret.AsSpan(), seed: payloadHash);
            BinaryPrimitives.WriteUInt32LittleEndian(finalLease.SpanFull.Slice(dataLen, UdpMacTagSize), computedTag);

            // 4. Commit the final datagram length (Token + Payload + Tag)
            finalLease.CommitLength(dataLen + UdpMacTagSize);

            return await this.SendRawAsync(finalLease.Memory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _onError(ex, originatingSocket);
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
