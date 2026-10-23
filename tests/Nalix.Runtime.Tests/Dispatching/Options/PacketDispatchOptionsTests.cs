// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;
using Nalix.Abstractions.Primitives;
using Nalix.Runtime.Routing;

namespace Nalix.Runtime.Tests.Dispatching.Options;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PacketDispatchOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void WithDispatchLoopCount_WhenOutOfRange_ThrowsArgumentOutOfRangeException(int value)
    {
        PacketDispatchOptions<TestPacket> options = new();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => { options.WithDispatchLoopCount(value); });
    }

    [Fact]
    public void WithDispatchLoopCount_WithNullOrValidValue_SetsDrainCount()
    {
        PacketDispatchOptions<TestPacket> options = new();

        _ = options.WithDispatchLoopCount(null);
        Assert.Equal(0, options.Drain.Count);

        _ = options.WithDispatchLoopCount(8);
        Assert.Equal(8, options.Drain.Count);
    }

    [Fact]
    public void WithMiddleware_WhenMiddlewareIsNull_ThrowsArgumentNullException()
    {
        PacketDispatchOptions<TestPacket> options = new();

        _ = Assert.Throws<ArgumentNullException>(() => { options.WithMiddleware(null!); });
    }

    /// <summary>
    /// Area 4: a packet handler that throws synchronously (before ever returning a ValueTask)
    /// must propagate the exception directly out of ExecuteResolvedHandlerAsync — the terminal
    /// fast path (PacketDispatchOptions.Execution.cs ExecuteTerminalHandler) has no try/catch
    /// around a synchronous-throw invoker.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithSynchronousThrowingHandler_PropagatesException()
    {
        PacketDispatchOptions<TestPacket> options = new();
        PacketHandler<TestPacket> descriptor = PacketHandlerFactory.CreateDescriptor(
            (_, _) => throw new InvalidOperationException("sync boom"));

        FakeConnection connection = new();
        try
        {
            Func<Task> act = async () => await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("sync boom");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Area 4: a packet handler that throws asynchronously (after an await) must be caught by
    /// PacketDispatchOptions.HandleDispatchExceptionAsync and converted into a FAIL control
    /// directive sent over the connection's transport, rather than propagated to the caller.
    /// The registered WithErrorHandling callback must also fire with the original exception.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithAsynchronousThrowingHandler_ContainsAndReportsException()
    {
        Exception? observedException = null;
        ushort observedOpCode = 0;

        PacketDispatchOptions<TestPacket> options = new PacketDispatchOptions<TestPacket>()
            .WithErrorHandling((ex, opCode) =>
            {
                observedException = ex;
                observedOpCode = opCode;
            });

        PacketHandler<TestPacket> descriptor = PacketHandlerFactory.CreateDescriptor(async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("async boom");
        });

        FakeConnection connection = new();
        try
        {
            await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            observedException.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("async boom");
            observedOpCode.Should().Be(descriptor.OpCode);
            connection.FakeTcp.SentMessages.Should().NotBeEmpty(
                "an asynchronously-thrown handler exception must be converted into a sent control directive");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Area 4: ExecuteTerminalHandler calls ct.ThrowIfCancellationRequested() synchronously
    /// before invoking the handler — a context whose token is already cancelled must throw
    /// immediately without ever invoking the handler delegate.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithAlreadyCancelledToken_ThrowsWithoutInvokingHandler()
    {
        PacketDispatchOptions<TestPacket> options = new();
        bool invoked = false;
        PacketHandler<TestPacket> descriptor = PacketHandlerFactory.CreateDescriptor((_, _) =>
        {
            invoked = true;
            return new ValueTask<object?>((object?)null);
        });

        FakeConnection connection = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        try
        {
            Func<Task> act = async () => await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false, token: cts.Token).AsTask();

            await act.Should().ThrowAsync<OperationCanceledException>();
            invoked.Should().BeFalse("a pre-cancelled token must short-circuit before the handler runs");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// A handler returning <c>IAsyncEnumerable&lt;TResponse&gt;</c> must have every yielded item sent,
    /// with the LAST item (and only the last) marked <see cref="IPacketStreamable.IsEndOfStream"/> —
    /// the runtime, not the handler, owns that bookkeeping. Covers the synchronous-completion path
    /// (<c>ExecuteTerminalHandler</c>'s fast path into <c>SendHandlerResponseAsync</c>).
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithSyncStreamHandler_SendsAllItemsAndFlagsOnlyLastAsEndOfStream()
    {
        PacketDispatchOptions<TestPacket> options = new();
        StreamTestPacket[] yielded = [new() { Value = 1 }, new() { Value = 2 }, new() { Value = 3 }];

        static async IAsyncEnumerable<StreamTestPacket> ToStream(StreamTestPacket[] items)
        {
            foreach (StreamTestPacket item in items)
            {
                yield return item;
            }

            await Task.CompletedTask;
        }

        PacketHandler<TestPacket> descriptor = PacketHandlerFactory.CreateDescriptor(
            (_, _) => new ValueTask<object?>(ToStream(yielded)));

        TestPacket request = new() { Header = new PacketHeader { SequenceId = 777 } };

        FakeConnection connection = new();
        try
        {
            await options.ExecuteResolvedHandlerAsync(
                descriptor, request, connection, reliable: true, encryptedOnWire: false).AsTask();

            connection.FakeTcp.SentMessages.Should().HaveCount(3,
                "every item the handler's stream yields must be sent as its own packet");
            yielded[0].IsEndOfStream.Should().BeFalse();
            yielded[1].IsEndOfStream.Should().BeFalse();
            yielded[2].IsEndOfStream.Should().BeTrue(
                "the runtime must mark the LAST yielded item as the end of the stream so the client's StreamAsync completes");

            // #392: every stream frame must go out via ReplyAsync (which echoes the request's
            // SequenceId onto response.Header in place before sending), not plain SendAsync —
            // otherwise a client's StreamAsync, which correlates replies by SequenceId, silently
            // discards every frame including the terminator. ReplyAsync mutates the same packet
            // instance that was yielded, so checking each item's Header.SequenceId after the send
            // proves which send path actually ran, without needing to decode wire bytes (this test's
            // StreamTestPacket.Serialize does not even emit a header).
            foreach (StreamTestPacket item in yielded)
            {
                item.Header.SequenceId.Should().Be(request.Header.SequenceId,
                    "every stream frame must echo the request's SequenceId so the client can correlate it");
            }
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Same contract as the synchronous test above, but for a handler that yields asynchronously
    /// (an <c>await</c> before the first item) — covers <c>AwaitHandlerAndRespondAsync</c>'s copy of
    /// the same stream branch.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithAsyncStreamHandler_SendsAllItemsAndFlagsOnlyLastAsEndOfStream()
    {
        PacketDispatchOptions<TestPacket> options = new();
        StreamTestPacket[] yielded = [new() { Value = 1 }, new() { Value = 2 }];

        static async IAsyncEnumerable<StreamTestPacket> ToStream(StreamTestPacket[] items)
        {
            foreach (StreamTestPacket item in items)
            {
                await Task.Yield();
                yield return item;
            }
        }

        PacketHandler<TestPacket> descriptor = PacketHandlerFactory.CreateDescriptor(async (_, _) =>
        {
            await Task.Yield();
            return (object?)ToStream(yielded);
        });

        FakeConnection connection = new();
        try
        {
            await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            connection.FakeTcp.SentMessages.Should().HaveCount(2);
            yielded[0].IsEndOfStream.Should().BeFalse();
            yielded[1].IsEndOfStream.Should().BeTrue();
        }
        finally
        {
            connection.Dispose();
        }
    }

    private sealed class StreamTestPacket : IPacket, IPacketStreamable
    {
        public int Length => 1;

        public PacketHeader Header { get; set; }

        public bool IsEndOfStream { get; set; }

        public int Value { get; init; }

        public byte[] Serialize() => [(byte)Value];

        public int Serialize(Span<byte> buffer)
        {
            buffer[0] = (byte)Value;
            return 1;
        }
    }
}
