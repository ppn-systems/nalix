// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.WebSockets;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Memory;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// A batch handed to the WebSocket transport keeps one WebSocket message per frame, but the messages
/// reach the underlying stream in one write when the connection has a <see cref="CoalescingWriteStream"/>.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class WebSocketBatchSendTests
{
    [Fact]
    public async Task SendAsyncCore_WithBatch_WritesSameBytesInOneStreamWrite()
    {
        byte[][] payloads = CreatePayloads(count: 5, length: 40);

        // Reference: the same messages sent one by one.
        RecordingStream sequentialInner = new();
        IConnection.ITransport sequential = CreateTransport(sequentialInner, out _);
        foreach (byte[] payload in payloads)
        {
            await sequential.SendAsyncCore(payload, CancellationToken.None);
        }

        RecordingStream batchedInner = new();
        IConnection.ITransport batched = CreateTransport(batchedInner, out _);
        await SendBatchAsync(batched, payloads);

        _ = sequentialInner.Writes.Should().Be(payloads.Length);
        _ = batchedInner.Writes.Should().Be(1);
        _ = batchedInner.ToArray().Should().Equal(sequentialInner.ToArray());
    }

    [Fact]
    public async Task SendAsyncCore_AfterBatch_WritesThroughAgain()
    {
        RecordingStream inner = new();
        IConnection.ITransport transport = CreateTransport(inner, out _);

        await SendBatchAsync(transport, CreatePayloads(count: 3, length: 40));
        int writesAfterBatch = inner.Writes;

        await transport.SendAsyncCore(new byte[40], CancellationToken.None);

        _ = writesAfterBatch.Should().Be(1);
        _ = inner.Writes.Should().Be(2);
    }

    [Fact]
    public async Task SendAsyncCore_WithBatchLargerThanBufferLimit_WritesThroughInSeveralWrites()
    {
        // 40 x 4 KB exceeds CoalescingWriteStream.MaxBufferedBytes, so the stream cannot hold it all back.
        byte[][] payloads = CreatePayloads(count: 40, length: 4096);

        RecordingStream sequentialInner = new();
        IConnection.ITransport sequential = CreateTransport(sequentialInner, out _);
        foreach (byte[] payload in payloads)
        {
            await sequential.SendAsyncCore(payload, CancellationToken.None);
        }

        RecordingStream batchedInner = new();
        IConnection.ITransport batched = CreateTransport(batchedInner, out _);
        await SendBatchAsync(batched, payloads);

        _ = batchedInner.Writes.Should().BeGreaterThan(1).And.BeLessThan(payloads.Length);
        _ = batchedInner.ToArray().Should().Equal(sequentialInner.ToArray());
    }

    [Fact]
    public async Task SendAsyncCore_WithBatchAndNoCoalescingStream_SendsEveryMessage()
    {
        byte[][] payloads = CreatePayloads(count: 4, length: 40);

        RecordingStream inner = new();
        WebSocket webSocket = WebSocket.CreateFromStream(inner, new WebSocketCreationOptions { IsServer = true });
        WebSocketConnection connection = new(webSocket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));

        await SendBatchAsync(connection.TCP, payloads);

        _ = inner.Writes.Should().Be(payloads.Length);
    }

    [Fact]
    public async Task FlushPendingAsync_AfterInterruptedBatch_WritesHeldBackBytes()
    {
        RecordingStream inner = new();
        await using CoalescingWriteStream stream = new(inner);

        stream.Cork();
        await stream.WriteAsync(new byte[] { 1, 2 });
        await stream.WriteAsync(new byte[] { 3 });
        _ = inner.Writes.Should().Be(0);

        stream.Uncork();
        await stream.FlushPendingAsync(CancellationToken.None);

        _ = inner.Writes.Should().Be(1);
        _ = inner.ToArray().Should().Equal(1, 2, 3);
    }

    private static IConnection.ITransport CreateTransport(RecordingStream inner, out WebSocketConnection connection)
    {
        CoalescingWriteStream stream = new(inner);
        WebSocket webSocket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        connection = new WebSocketConnection(webSocket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0), timingWheel: null, stream);
        return connection.TCP;
    }

    private static async Task SendBatchAsync(IConnection.ITransport transport, byte[][] payloads)
    {
        IBufferLease[] frames = new IBufferLease[payloads.Length];
        for (int i = 0; i < payloads.Length; i++)
        {
            frames[i] = BufferLease.CopyFrom(payloads[i]);
        }

        try
        {
            await transport.SendAsyncCore(new ReadOnlyMemory<IBufferLease>(frames), CancellationToken.None);
        }
        finally
        {
            foreach (IBufferLease frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    private static byte[][] CreatePayloads(int count, int length)
    {
        Random random = new(42);
        byte[][] payloads = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            payloads[i] = new byte[length];
            random.NextBytes(payloads[i]);
        }

        return payloads;
    }

    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    /// <summary>Records what the WebSocket writes; never yields data to read.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _written = new();

        public int Writes { get; private set; }

        public byte[] ToArray() => _written.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            this.Writes++;
            _written.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            this.Writes++;
            _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
