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

    [Fact]
    public async Task SendAsyncCore_WithBatchOnClosedWebSocket_SendsNothing()
    {
        RecordingStream inner = new();
        IConnection.ITransport transport = CreateTransport(inner, out WebSocketConnection connection);
        await ((WebSocketTransport)transport).WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        int writesAfterClose = inner.Writes;

        await SendBatchAsync(transport, CreatePayloads(count: 3, length: 40));

        _ = inner.Writes.Should().Be(writesAfterClose);
        GC.KeepAlive(connection);
    }

    [Fact]
    public async Task SendAsyncCore_WhenBatchIsInterrupted_StillWritesTheMessagesAlreadyHeldBack()
    {
        byte[][] payloads = CreatePayloads(count: 3, length: 40);

        RecordingStream referenceInner = new();
        IConnection.ITransport reference = CreateTransport(referenceInner, out _);
        await reference.SendAsyncCore(payloads[0], CancellationToken.None);

        RecordingStream inner = new();
        IConnection.ITransport transport = CreateTransport(inner, out _);
        IBufferLease[] frames =
        [
            BufferLease.CopyFrom(payloads[0]),
            new ThrowingLease(),
            BufferLease.CopyFrom(payloads[2]),
        ];

        try
        {
            Func<Task> send = () => transport.SendAsyncCore(new ReadOnlyMemory<IBufferLease>(frames), CancellationToken.None).AsTask();
            _ = await send.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            frames[0].Dispose();
            frames[2].Dispose();
        }

        // The first message was held back when the second one failed; it must not stay stuck in the buffer.
        _ = inner.ToArray().Should().Equal(referenceInner.ToArray());

        // And the stream is no longer holding writes back.
        await transport.SendAsyncCore(payloads[2], CancellationToken.None);
        _ = inner.Writes.Should().Be(2);
    }

    [Fact]
    public void Write_Synchronous_FollowsTheSameHoldBackRules()
    {
        RecordingStream inner = new();
        using CoalescingWriteStream stream = new(inner);

        stream.Write([1], 0, 1);
        _ = inner.Writes.Should().Be(1);

        stream.Cork();
        stream.Write([2, 3], 0, 2);
        stream.Write([4], 0, 1);
        _ = inner.Writes.Should().Be(1);

        stream.Uncork();
        stream.Write([5], 0, 1);

        _ = inner.Writes.Should().Be(2);
        _ = inner.ToArray().Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task WriteAsync_WhileCorkedPastBufferLimit_WritesThroughAndKeepsOrder()
    {
        RecordingStream inner = new();
        await using CoalescingWriteStream stream = new(inner);
        byte[] chunk = new byte[CoalescingWriteStream.MaxBufferedBytes / 2];
        new Random(7).NextBytes(chunk);

        stream.Cork();
        await stream.WriteAsync(chunk);
        _ = inner.Writes.Should().Be(0);

        await stream.WriteAsync(chunk, 0, chunk.Length, CancellationToken.None);
        _ = inner.Writes.Should().Be(1);
        _ = inner.ToArray().Should().Equal([.. chunk, .. chunk]);
    }

    [Fact]
    public async Task Read_PassesThroughToInnerStream()
    {
        using MemoryStream inner = new([9, 8, 7, 6]);
        await using CoalescingWriteStream stream = new(inner);
        byte[] buffer = new byte[2];

        _ = stream.Read(buffer, 0, 1).Should().Be(1);
        _ = (await stream.ReadAsync(buffer.AsMemory(1, 1))).Should().Be(1);
        _ = buffer.Should().Equal(9, 8);
        _ = (await stream.ReadAsync(buffer, 0, 2, CancellationToken.None)).Should().Be(2);
        _ = buffer.Should().Equal(7, 6);
    }

    [Fact]
    public async Task Dispose_WithHeldBackBytes_DisposesInnerStream()
    {
        RecordingStream inner = new();
        CoalescingWriteStream stream = new(inner);
        stream.Cork();
        await stream.WriteAsync(new byte[] { 1 });

        await stream.DisposeAsync();

        _ = inner.Disposed.Should().BeTrue();
        _ = inner.Writes.Should().Be(0);
    }

    private sealed class ThrowingLease : IBufferLease
    {
        public int Length => 40;
        public bool IsReliable { get; set; }
        public bool EncryptedOnWire { get; set; }
        public int Capacity => 40;
        public Span<byte> Span => throw new InvalidOperationException("lease failed");
        public Span<byte> SpanFull => throw new InvalidOperationException("lease failed");
        public ReadOnlyMemory<byte> Memory => throw new InvalidOperationException("lease failed");
        public void Retain() { }
        public void CommitLength(int length) { }
        public bool ReleaseOwnership(out byte[]? buffer, out int start, out int length)
        {
            buffer = null;
            start = 0;
            length = 0;
            return false;
        }
        public void Dispose() { }
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

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            this.Disposed = true;
            base.Dispose(disposing);
        }

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
