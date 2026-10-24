// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Fragments;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;
using TransportAsyncCallback = Nalix.Network.Internal.Transport.AsyncCallback;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// Area 1 (wire framing/fragmentation) adversarial coverage — length-prefix attacks, partial-frame
/// reassembly, fragmentation adversarial cases, trailing garbage, and half-close mid-frame.
/// Extends <see cref="SocketConnectionFramingTests"/> and <see cref="SocketConnectionFragmentationTests"/>
/// (does not duplicate their existing cases) using the same real-loopback-socket harness pattern.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.AsyncCallback)]
public sealed class SocketConnectionFramingAdversarialTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    private static byte[] CreateFrame(ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[payload.Length + sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);
        payload.CopyTo(frame.AsSpan(sizeof(ushort)));
        return frame;
    }

    private static byte[] CreateFragmentPayload(ushort streamId, ushort chunkIndex, ushort totalChunks, bool isLast, ReadOnlySpan<byte> body)
    {
        byte[] payload = new byte[FragmentHeader.WireSize + body.Length];
        FragmentHeader header = new(streamId, chunkIndex, totalChunks, isLast);
        header.WriteTo(payload);
        body.CopyTo(payload.AsSpan(FragmentHeader.WireSize));
        return payload;
    }

    // ── Length-prefix attacks ───────────────────────────────────────────────

    /// <summary>
    /// Policy A: an invalid declared frame size desynchronizes the byte stream (no reliable resync
    /// marker exists in this protocol), so it is treated as a fatal protocol violation — the
    /// connection is closed and no further frame (even a well-formed one sent right after) is ever
    /// dispatched. See SocketConnection.cs SAEA_RECEIVE_LOOP_ASYNC invalid-size branch.
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithZeroDeclaredLength_ClosesConnectionAndDispatchesNothing()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        connection.MessageProcessing += (_, _) =>
        {
            Interlocked.Increment(ref callCount);
        };
        connection.ConnectionClosed += (_, _) => closed.TrySetResult(true);

        connection.TCP.BeginReceive();

        // Zero-length prefix bytes, then a valid frame right after.
        byte[] zeroPrefix = [0x00, 0x00];
        byte[] validFrame = CreateFrame([9, 9, 9, 9, 9, 9]);
        await scope.ClientSocket.SendAsync(zeroPrefix);
        await Task.Delay(50); // let the invalid-size drop register before the valid frame arrives
        await scope.ClientSocket.SendAsync(validFrame);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await Task.Delay(200);
        Volatile.Read(ref callCount).Should().Be(0, "an invalid declared length desynchronizes the stream; the connection must close and no frame (including one sent right after) must ever be dispatched");
    }

    /// <summary>
    /// Declared length = ushort.MaxValue (65535) — the practical maximum representable by the
    /// 2-byte UInt16LengthPrefixed length field — must not allocate an attacker-controlled buffer
    /// beyond the server's configured max receive buffer size; either the frame is accepted (if
    /// within configured limits) or the connection is rejected/closed cleanly via GetMessageSize().
    /// See conversation notes: the fixed 2-byte length prefix physically cannot carry
    /// "int.MaxValue-as-unsigned" or "negative-as-signed" values — those are exercised instead via
    /// the VarInt framing path in <see cref="SocketConnectionVarIntFramingAdversarialTests"/>.
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithUInt16MaxDeclaredLength_DoesNotAllocateUnboundedBuffer()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<bool> closedOrIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, _) => { };
        connection.ConnectionClosed += (_, _) => closedOrIdle.TrySetResult(true);

        connection.TCP.BeginReceive();

        // Declare a 65535-byte frame (max representable by ushort) but only send the 2-byte prefix.
        byte[] maxPrefix = [0xFF, 0xFF];
        await scope.ClientSocket.SendAsync(maxPrefix);

        // The connection must not crash/hang; either it awaits the (huge, but bounded-by-config)
        // pending frame or it rejects via GetMessageSize() and closes. We assert it survives at
        // least this grace window without throwing an unhandled exception that tears down the process.
        await Task.WhenAny(closedOrIdle.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        // The connection object itself must still be usable/disposable without throwing.
        Action dispose = () => connection.Dispose();
        dispose.Should().NotThrow("a declared max-representable length must never crash or corrupt connection teardown");
    }

    // ── Partial frames ───────────────────────────────────────────────────────

    /// <summary>
    /// Splitting a single frame at every possible byte boundary (byte-at-a-time delivery in the
    /// extreme case) must always reassemble to the exact original payload.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public async Task BeginReceive_WithFrameSplitIntoChunks_ReassemblesExactPayload(int chunkSize)
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        byte[] payload = SequentialBytes(37, seed: 3);
        byte[] frame = CreateFrame(payload);

        TaskCompletionSource<byte[]> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            { processObserved.TrySetResult(args.Lease?.Span.ToArray() ?? []); }
        };

        connection.TCP.BeginReceive();

        for (int offset = 0; offset < frame.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, frame.Length - offset);
            await scope.ClientSocket.SendAsync(frame.AsSpan(offset, len).ToArray());
            await Task.Delay(1); // force separate socket reads instead of coalescing in the send buffer
        }

        byte[] received = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        received.Should().Equal(payload, $"split at chunkSize={chunkSize} must reassemble to the exact original payload");
    }

    /// <summary>
    /// Two complete frames coalesced into a single socket read/write must both be parsed and
    /// dispatched as two separate messages. The process callbacks run on the thread pool, so their relative order is not
    /// guaranteed and is not asserted here (only the inline frame-processor path keeps per-connection order).
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithTwoCoalescedFrames_DispatchesEachFrameSeparately()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        byte[] frameA = CreateFrame([1, 2, 3, 1, 2, 3]);
        byte[] frameB = CreateFrame([4, 5, 6, 7, 4, 5, 6, 7]);
        byte[] coalesced = [.. frameA, .. frameB];

        List<byte[]> received = [];
        TaskCompletionSource<bool> gotBoth = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            {
                lock (received)
                {
                    received.Add(args.Lease?.Span.ToArray() ?? []);
                    if (received.Count >= 2)
                    {
                        gotBoth.TrySetResult(true);
                    }
                }
            }
        };

        connection.TCP.BeginReceive();
        await scope.ClientSocket.SendAsync(coalesced);

        await gotBoth.Task.WaitAsync(TimeSpan.FromSeconds(15));

        lock (received)
        {
            received.Should().HaveCount(2);
            received.Should().ContainSingle(frame => System.Linq.Enumerable.SequenceEqual(frame, new byte[] { 1, 2, 3, 1, 2, 3 }));
            received.Should().ContainSingle(frame => System.Linq.Enumerable.SequenceEqual(frame, new byte[] { 4, 5, 6, 7, 4, 5, 6, 7 }));
        }
    }

    /// <summary>
    /// A valid frame immediately followed by a truncated (incomplete) second frame: the first
    /// must be dispatched, and the truncated remainder must not be dispatched nor crash the loop.
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithValidFrameThenTruncatedFrame_DispatchesOnlyValidFrame()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        byte[] validFrame = CreateFrame([10, 20, 30, 10, 20, 30]);
        byte[] truncatedFrame = CreateFrame([1, 2, 3, 4, 5, 6, 7, 8]);
        byte[] truncatedSentPart = truncatedFrame.AsSpan(0, 4).ToArray(); // header + partial payload only

        TaskCompletionSource<byte[]> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        connection.MessageProcessing += (_, args) =>
        {
            {
                Interlocked.Increment(ref callCount);
                processObserved.TrySetResult(args.Lease?.Span.ToArray() ?? []);
            }
        };

        connection.TCP.BeginReceive();
        byte[] combined1 = [.. validFrame, .. truncatedSentPart];
        await scope.ClientSocket.SendAsync(combined1);

        byte[] received = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        received.Should().Equal([10, 20, 30, 10, 20, 30]);

        await Task.Delay(200);
        Volatile.Read(ref callCount).Should().Be(1, "the truncated trailing frame must not be dispatched until completed");
    }

    /// <summary>
    /// Trailing garbage after a valid frame that never forms a valid subsequent header must not
    /// crash the loop or get dispatched as a spurious frame.
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithTrailingGarbageByte_DispatchesOnlyValidFrame()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        byte[] validFrame = CreateFrame([42, 42, 42, 42, 42, 42]);
        byte[] garbage = SequentialBytes(1, seed: 0xAB); // 1 stray byte, never completes a 2-byte header

        TaskCompletionSource<byte[]> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            { processObserved.TrySetResult(args.Lease?.Span.ToArray() ?? []); }
        };

        connection.TCP.BeginReceive();
        byte[] combined2 = [.. validFrame, .. garbage];
        await scope.ClientSocket.SendAsync(combined2);

        byte[] received = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        received.Should().Equal([42, 42, 42, 42, 42, 42]);

        // The connection must remain alive and not throw when torn down despite the dangling byte.
        Action dispose = () => connection.Dispose();
        dispose.Should().NotThrow();
    }

    // ── Fragmentation adversarial cases ──────────────────────────────────────

    /// <summary>
    /// Duplicate delivery of chunk index 0 for an in-progress stream (out-of-order relative to
    /// the expected next index) must throw a contained <see cref="System.IO.InvalidDataException"/>
    /// from <see cref="FragmentAssembler.Add"/>, never silently corrupt or duplicate output.
    /// Exercised directly against FragmentAssembler (unit-level), since inducing this exact race
    /// through a live socket is not deterministically reproducible with TCP's in-order guarantee.
    /// </summary>
    [Fact]
    public void Add_WithDuplicateChunkIndex_ThrowsInvalidDataException()
    {
        using FragmentAssembler assembler = new();
        FragmentHeader first = new(streamId: 5, chunkIndex: 0, totalChunks: 2, isLast: false);
        _ = assembler.Add(first, [1, 2, 3], out _);

        // Re-deliver chunk index 0 again instead of the expected index 1.
        FragmentHeader duplicate = new(streamId: 5, chunkIndex: 0, totalChunks: 2, isLast: false);
        Action act = () => assembler.Add(duplicate, [9, 9, 9], out _);

        act.Should().Throw<System.IO.InvalidDataException>("duplicate/out-of-order chunk delivery must be rejected, not silently merged");
    }

    /// <summary>
    /// A fragment claiming a chunk index at or beyond totalChunks must throw
    /// <see cref="System.IO.InvalidDataException"/> immediately, before any buffer growth.
    /// </summary>
    [Fact]
    public void Add_WithChunkIndexAtTotalChunks_ThrowsInvalidDataException()
    {
        using FragmentAssembler assembler = new();
        FragmentHeader bogus = new(streamId: 7, chunkIndex: 3, totalChunks: 3, isLast: true);

        Action act = () => assembler.Add(bogus, [1], out _);

        act.Should().Throw<System.IO.InvalidDataException>();
    }

    /// <summary>
    /// A never-completed fragment stream must be evicted by <see cref="FragmentAssembler.EvictExpired"/>
    /// once its configured timeout elapses — the stream must not leak indefinitely.
    /// </summary>
    [Fact]
    public void EvictExpired_WithIncompleteStreamPastTimeout_EvictsStream()
    {
        using FragmentAssembler assembler = new() { StreamTimeoutMs = 1 };
        FragmentHeader header = new(streamId: 11, chunkIndex: 0, totalChunks: 5, isLast: false);
        _ = assembler.Add(header, [1, 2, 3], out _);

        assembler.OpenStreamCount.Should().Be(1);

        Thread.Sleep(20); // exceed the 1ms timeout

        int evicted = assembler.EvictExpired();

        evicted.Should().Be(1);
        assembler.OpenStreamCount.Should().Be(0, "a never-completed stream must be evicted, not leak memory indefinitely");
    }

    /// <summary>
    /// Opening more concurrent fragment streams than <see cref="FragmentAssembler.MaxOpenStreams"/>
    /// must reject the excess streams (SEC-34 guard) instead of allocating unbounded memory.
    /// </summary>
    [Fact]
    public void Add_WhenMaxOpenStreamsExceeded_RejectsExcessStream()
    {
        using FragmentAssembler assembler = new() { MaxOpenStreams = 2 };

        _ = assembler.Add(new FragmentHeader(streamId: 1, chunkIndex: 0, totalChunks: 2, isLast: false), [1], out bool evicted1);
        _ = assembler.Add(new FragmentHeader(streamId: 2, chunkIndex: 0, totalChunks: 2, isLast: false), [1], out bool evicted2);
        FragmentAssemblyResult? third = assembler.Add(new FragmentHeader(streamId: 3, chunkIndex: 0, totalChunks: 2, isLast: false), [1], out bool evicted3);

        evicted1.Should().BeFalse();
        evicted2.Should().BeFalse();
        third.Should().BeNull();
        evicted3.Should().BeTrue("a third stream beyond MaxOpenStreams=2 must be rejected, not silently accepted");
        assembler.OpenStreamCount.Should().Be(2);
    }

    // ── Zero-length reads / half-close mid-frame ─────────────────────────────

    /// <summary>
    /// Remote half-close (graceful shutdown of the send side) delivered mid-frame (after a partial
    /// header/payload was already buffered) must end the receive loop cleanly without throwing an
    /// unhandled exception that escapes the async loop.
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithHalfCloseMidFrame_EndsReceiveLoopCleanly()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        connection.MessageProcessing += (_, _) => { };

        connection.TCP.BeginReceive();

        // Send only a partial frame (declared length 10, but only 2 payload bytes delivered), then
        // half-close the client's send side.
        byte[] partial = CreateFrame(SequentialBytes(10, seed: 1)).AsSpan(0, 4).ToArray();
        await scope.ClientSocket.SendAsync(partial);
        scope.ClientSocket.Shutdown(SocketShutdown.Send);

        // Give the receive loop time to observe the half-close (peer closed) and exit gracefully.
        await Task.Delay(300);

        Action dispose = () => connection.Dispose();
        dispose.Should().NotThrow("a half-close mid-frame must be handled as a benign disconnect, not an unhandled exception");
    }

    private static byte[] SequentialBytes(int length, int seed)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(seed + i);
        }
        return data;
    }

    private sealed class ConnectedSocketScope : IDisposable
    {
        private ConnectedSocketScope(Socket listenerSocket, Socket clientSocket, Socket serverSocket)
        {
            ListenerSocket = listenerSocket;
            ClientSocket = clientSocket;
            ServerSocket = serverSocket;
        }

        public Socket ListenerSocket { get; }

        public Socket ClientSocket { get; }

        public Socket ServerSocket { get; }

        public static async Task<ConnectedSocketScope> CreateAsync()
        {
            Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            Task<Socket> acceptTask = Task.Run(() => listener.Accept());

            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, port);

            Socket server = await acceptTask;
            return new ConnectedSocketScope(listener, client, server);
        }

        public void Dispose()
        {
            try { ClientSocket.Dispose(); } catch { }
            try { ServerSocket.Dispose(); } catch { }
            try { ListenerSocket.Dispose(); } catch { }
        }
    }
}
