// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Transforms;
using Nalix.Environment.Configuration;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;
using Nalix.Environment.Sequencing;
using Nalix.SDK.Options;

namespace Nalix.SDK.Transport.Internal.Udp;

/// <summary>
/// Handles receiving and processing UDP datagrams.
/// Responsible for: buffer management, inbound pipeline (decrypt + decompress), 
/// and dispatching to <see cref="UdpSession"/>.
/// </summary>
internal sealed class UdpFrameReader : IDisposable
{
    private static readonly SequenceOptions s_sequenceOptions = ConfigurationManager.Instance.Get<SequenceOptions>();

    private readonly SessionState _state;
    private readonly Func<Socket> _getSocket;
    private readonly SequenceCounter _sequence;
    internal SequenceCounter Sequence => _sequence;
    private readonly TransportOptions _options;
    private readonly Action<Exception, Socket?> _onError;
    private readonly Action<IBufferLease> _onMessageReceived;
    private readonly Func<Func<ReadOnlyMemory<byte>, Task>?> _getOnMessageAsync;

    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="UdpFrameReader"/> class.
    /// </summary>
    /// <param name="getSocket">Delegate to get current socket.</param>
    /// <param name="options">Transport options.</param>
    /// <param name="state">Session state.</param>
    /// <param name="onMessageReceived">Sync callback for <see cref="UdpSession.OnMessageReceived"/>.</param>
    /// <param name="getOnMessageAsync">Gets the current async callback for <see cref="UdpSession.OnMessageAsync"/>.</param>
    /// <param name="onError">Error callback.</param>
    public UdpFrameReader(
        Func<Socket> getSocket,
        TransportOptions options,
        SessionState state,
        Action<IBufferLease> onMessageReceived,
        Func<Func<ReadOnlyMemory<byte>, Task>?> getOnMessageAsync,
        Action<Exception, Socket?> onError)
    {
        _sequence = new SequenceCounter();
        _getSocket = getSocket ?? throw new ArgumentNullException(nameof(getSocket));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _onMessageReceived = onMessageReceived ?? throw new ArgumentNullException(nameof(onMessageReceived));
        _getOnMessageAsync = getOnMessageAsync ?? throw new ArgumentNullException(nameof(getOnMessageAsync));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    /// <summary>
    /// Main receive loop for UDP datagrams.
    /// </summary>
    public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Socket? socket = _getSocket();
        if (socket == null)
        {
            return;
        }

        int bufferSize = _options.MaxUdpDatagramSize;
        byte[]? rawBuffer = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                rawBuffer = BufferLease.ByteArrayPool.Rent(bufferSize);

                try
                {
                    int received = await socket.ReceiveAsync(rawBuffer, SocketFlags.None, cancellationToken)
                                               .ConfigureAwait(false);

                    if (received <= 0)
                    {
                        continue;
                    }

                    IBufferLease lease = BufferLease.TakeOwnership(rawBuffer, 0, received);
                    rawBuffer = null;

                    await this.ProcessDatagramAsync(lease, cancellationToken, socket)
                              .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _onError(ex, socket);
                    }
                    break;
                }
                finally
                {
                    if (rawBuffer != null)
                    {
                        BufferLease.ByteArrayPool.Return(rawBuffer);
                        rawBuffer = null;
                    }
                }
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _onError(ex, socket);
        }
    }

    private async Task ProcessDatagramAsync(IBufferLease datagram, CancellationToken ct, Socket socket)
    {
        IBufferLease original = datagram;
        uint? seq = null;

        try
        {
            // 1) Verify XxHash32
            if (datagram.Length < PacketHeader.Size + 4)
            {
                return; // Drop short or spoofed packets
            }

            uint receivedHash = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Span[^4..]);
            int dataLen = datagram.Length - 4;

            if (datagram.Capacity >= dataLen + Bytes32.Size)
            {
                _state.Secret.AsSpan().CopyTo(datagram.SpanFull[dataLen..]);
                uint computedHash = XxHash32.Compute(datagram.SpanFull[..(dataLen + Bytes32.Size)]);
                datagram.SpanFull.Slice(dataLen, Bytes32.Size).Clear(); // [SECURITY] scrub the copied secret
                if (computedHash != receivedHash)
                {
                    return; // Drop spoofed packet
                }
            }
            else
            {
                byte[] temp = BufferLease.ByteArrayPool.Rent(dataLen + Bytes32.Size);
                datagram.Span[..dataLen].CopyTo(temp);
                _state.Secret.AsSpan().CopyTo(temp.AsSpan(dataLen));
                uint computedHash = XxHash32.Compute(temp.AsSpan(0, dataLen + Bytes32.Size));
                BufferLease.ByteArrayPool.Return(temp, clearArray: true); // [SECURITY] holds the secret
                if (computedHash != receivedHash)
                {
                    return; // Drop spoofed packet
                }
            }

            // Strip the 4-byte tag from the payload
            datagram.CommitLength(dataLen);

            // 2) Decompress / Decrypt
            FramePipeline.ProcessInbound(ref datagram, _state.Secret.AsSpan(), _options.Algorithm, out seq);

            if (!_sequence.IsValid(seq, s_sequenceOptions.UdpWindow))
            {
                if (Codec.DiagnosticsEvents.Source.IsEnabled(Codec.DiagnosticsEvents.Sequence.Rejected))
                {
                    Codec.DiagnosticsEvents.Write(Codec.DiagnosticsEvents.Sequence.Rejected, new
                    {
                        Transport = "UDP",
                        ReceivedSeq = seq,
                        CurrentSeq = _sequence.Current()
                    });
                }
                return; // Duplicate datagram, already processed
            }

            // Dispatch
            await this.DispatchMessageAsync(datagram, ct, socket).ConfigureAwait(false);
        }
        finally
        {
            if (seq.HasValue)
            {
                _sequence.UpdateTo(seq.Value);
            }

            if (!ReferenceEquals(datagram, original))
            {
                datagram.Dispose();
            }
            original.Dispose();
        }
    }

    private async Task DispatchMessageAsync(IBufferLease lease, CancellationToken _, Socket socket)
    {
        // Sync handler (hot path)
        _onMessageReceived?.Invoke(lease);

        if (_getOnMessageAsync() is { } onMessageAsync)
        {
            lease.Retain();

            try
            {
                await onMessageAsync(lease.Memory).ConfigureAwait(false);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                // Report the socket this datagram was actually received on, not whatever
                // _getSocket() resolves to now — a fast reconnect racing this handler failure can
                // have already moved the field to a newer connection's socket.
                _onError(ex, socket);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }
    }
}
