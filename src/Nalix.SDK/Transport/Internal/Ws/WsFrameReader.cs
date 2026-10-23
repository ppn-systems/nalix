// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Net.WebSockets;
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

namespace Nalix.SDK.Transport.Internal.Ws;

internal sealed class WsFrameReader : IDisposable
{
    private static readonly FragmentOptions s_fragmentOptions = ConfigurationManager.Instance.Get<FragmentOptions>();
    private static readonly SequenceOptions s_sequenceOptions = ConfigurationManager.Instance.Get<SequenceOptions>();

    private readonly Action<Exception, ClientWebSocket?> _onError;
    private readonly Action<IBufferLease> _onMessage;
    private readonly Func<ClientWebSocket> _getSocket;

    private readonly SessionState _state;
    private readonly SequenceCounter _sequence;
    internal SequenceCounter Sequence => _sequence;
    private readonly TransportOptions _options;
    private readonly WebSocketTransportOptions _webSocketOptions;

    private readonly FragmentAssembler _assembler = new()
    {
        MaxStreamBytes = s_fragmentOptions.MaxReassemblyBytes,
        StreamTimeoutMs = s_fragmentOptions.ReassemblyTimeoutMs
    };

    private int _disposed;

    public WsFrameReader(
        Func<ClientWebSocket> getSocket,
        TransportOptions options,
        SessionState state,
        WebSocketTransportOptions webSocketOptions,
        Action<IBufferLease> onMessage,
        Action<Exception, ClientWebSocket?> onError)
    {
        _sequence = new SequenceCounter();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _webSocketOptions = webSocketOptions ?? throw new ArgumentNullException(nameof(webSocketOptions));
        _getSocket = getSocket ?? throw new ArgumentNullException(nameof(getSocket));
        _onMessage = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    public async Task ReceiveLoopAsync(CancellationToken ct)
    {
        ClientWebSocket socket = _getSocket();
        byte[] buffer = BufferLease.ByteArrayPool.Rent(8192);
        Exception? disconnectReason = null;

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    disconnectReason = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The WebSocket session was closed by the remote host.");
                    break;
                }

                if (result.EndOfMessage)
                {
                    // Fast path: the whole message fits in the rented buffer
                    if (result.Count > _webSocketOptions.MaxMessageSize)
                    {
                        throw new InvalidOperationException(
                            $"WebSocket message size {result.Count} exceeds maximum {_webSocketOptions.MaxMessageSize}.");
                    }

                    this.PROCESS_FRAME(buffer.AsSpan(0, result.Count), socket);
                }
                else
                {
                    // Slow path: large message spanning multiple frames
                    await this.RECEIVE_LARGE_FRAME_ASYNC(socket, buffer, result.Count, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            disconnectReason = ex;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            disconnectReason = ex;
        }
        finally
        {
            _assembler.Clear();
            BufferLease.ByteArrayPool.Return(buffer);
            if (disconnectReason != null)
            {
                _onError?.Invoke(disconnectReason, socket);
            }
            else if (!ct.IsCancellationRequested)
            {
                _onError?.Invoke(new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The WebSocket session was disconnected."), socket);
            }
        }
    }

    private async Task RECEIVE_LARGE_FRAME_ASYNC(ClientWebSocket socket, byte[] initialBuffer, int initialCount, CancellationToken ct)
    {
        using MemoryStream ms = new();
        await ms.WriteAsync(initialBuffer.AsMemory(0, initialCount), ct).ConfigureAwait(false);

        if (ms.Length > _webSocketOptions.MaxMessageSize)
        {
            throw new InvalidOperationException(
                $"WebSocket message size {ms.Length} exceeds maximum {_webSocketOptions.MaxMessageSize}.");
        }

        byte[] tempBuffer = BufferLease.ByteArrayPool.Rent(65536);
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(tempBuffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                await ms.WriteAsync(tempBuffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);

                if (ms.Length > _webSocketOptions.MaxMessageSize)
                {
                    throw new InvalidOperationException(
                        $"WebSocket message size {ms.Length} exceeds maximum {_webSocketOptions.MaxMessageSize}.");
                }
            }
            while (!result.EndOfMessage);

            if (ms.TryGetBuffer(out ArraySegment<byte> fullBuffer))
            {
                this.PROCESS_FRAME(fullBuffer.AsSpan(), socket);
            }
            else
            {
                this.PROCESS_FRAME(ms.ToArray(), socket);
            }
        }
        finally
        {
            BufferLease.ByteArrayPool.Return(tempBuffer);
        }
    }

    private void PROCESS_FRAME(ReadOnlySpan<byte> frameData, ClientWebSocket socket)
    {
        if (FragmentAssembler.IsFragmentedFrame(frameData, out FragmentHeader header))
        {
            try
            {
                ReadOnlySpan<byte> chunkBody = frameData[FragmentHeader.WireSize..];
                FragmentAssemblyResult? assembled = _assembler.Add(header, chunkBody, out _);
                if (assembled is not null)
                {
                    this.PROCESS_ASSEMBLED_FRAME(assembled.Value.Lease, socket);
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                _onError?.Invoke(ex, socket);
            }
            return;
        }

        BufferLease lease = BufferLease.Rent(frameData.Length);
        frameData.CopyTo(lease.SpanFull);
        lease.CommitLength(frameData.Length);

        this.PROCESS_ASSEMBLED_FRAME(lease, socket);
    }

    private void PROCESS_ASSEMBLED_FRAME(IBufferLease lease, ClientWebSocket socket)
    {
        IBufferLease original = lease;
        uint? seq = null;
        try
        {
            FramePipeline.ProcessInbound(
                ref lease,
                _state.Secret.AsSpan(),
                _options.Algorithm,
                out seq);

            if (!_sequence.IsValid(seq, s_sequenceOptions.TcpWindow))
            {
                if (Codec.DiagnosticsEvents.Source.IsEnabled(Codec.DiagnosticsEvents.Sequence.Rejected))
                {
                    Codec.DiagnosticsEvents.Write(Codec.DiagnosticsEvents.Sequence.Rejected, new
                    {
                        Transport = "WebSocket",
                        ReceivedSeq = seq,
                        CurrentSeq = _sequence.Current()
                    });
                }
                return;
            }

            if (seq.HasValue)
            {
                _sequence.UpdateTo(seq.Value);
            }

            _onMessage(lease);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // Report the socket this frame was actually read from, not whatever _getSocket()
            // resolves to now — a fast reconnect racing this decode failure can have already moved
            // the field to a newer connection's socket (see the same fix in WsFrameSender.SEND_CORE).
            _onError?.Invoke(ex, socket);
        }
        finally
        {
            if (!ReferenceEquals(lease, original))
            {
                lease.Dispose();
            }

            original.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _assembler.Dispose();
    }
}
