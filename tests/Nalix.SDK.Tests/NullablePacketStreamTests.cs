// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Tests;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed partial class NullablePacketStreamTests
{
    public NullablePacketStreamTests()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task StreamAsyncWhenTerminalNullableFieldIsNullYieldsItemAndCompletes()
    {
        NullableStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 1001 };

        NullableStreamItem response = new()
        {
            IsEndOfStream = true,
            Capacity = null,
            Duration = TimeSpan.FromSeconds(1)
        };
        response.Header = response.Header with { SequenceId = request.Header.SequenceId };

        NullableStreamItem result = await ReadSingleStreamItemAsync(request, response);

        Assert.True(result.IsEndOfStream);
        Assert.Null(result.Capacity);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Duration);
    }

    [Fact]
    public async Task StreamAsyncWhenTerminalNullableFieldHasValueYieldsItemAndCompletes()
    {
        NullableStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 1002 };

        NullableStreamItem response = new()
        {
            IsEndOfStream = true,
            Capacity = 128,
            Duration = null
        };
        response.Header = response.Header with { SequenceId = request.Header.SequenceId };

        NullableStreamItem result = await ReadSingleStreamItemAsync(request, response);

        Assert.True(result.IsEndOfStream);
        Assert.Equal(128, result.Capacity);
        Assert.Null(result.Duration);
    }

    [Fact]
    public async Task StreamAsyncWhenRequestSequenceIdIsUnsetStillReceivesReply()
    {
        // Regression test: the request is left at SequenceId 0, exactly as a caller who forgot to
        // stamp it would send it. The response below is stamped with the id a *fresh* session's own
        // outbound counter produces on its first send (1) — the same value TcpSession/UdpSession/
        // WebSocketSession's SendAsync would have stamped the wire packet with, had StreamAsync not
        // pre-stamped it itself. Before the fix, StreamAsync latched expectedSeqId = 0 (the request's
        // unset value) before ever sending, so this reply — matching what actually went out — would
        // never equal expectedSeqId and would be silently disposed: no exception, the stream just
        // never completes.
        NullableStreamRequest request = new();
        Assert.Equal(0, request.Header.SequenceId);

        NullableStreamItem response = new() { IsEndOfStream = true, Capacity = 42 };
        response.Header = response.Header with { SequenceId = 1 };

        FakeStreamSession session = new(response);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

        List<NullableStreamSnapshot> snapshots = [];
        await foreach (NullableStreamItem item in session.StreamAsync<NullableStreamItem>(request, ct: cts.Token))
        {
            snapshots.Add(new NullableStreamSnapshot(
                item.Header.SequenceId, item.IsEndOfStream, item.Capacity, item.Duration));
        }

        // The request must have been stamped with a real id before send, matching the reply's id.
        Assert.Equal(1, request.Header.SequenceId);
        NullableStreamSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(42, snapshot.Capacity);
    }

    [Fact]
    public async Task StreamAsyncWhenSequenceDoesNotMatchDisposesIgnoredPacket()
    {
        NullableStreamItem.DisposeCount = 0;

        NullableStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 1003 };

        NullableStreamItem ignored = new()
        {
            IsEndOfStream = false,
            Capacity = 64,
            Duration = null
        };
        ignored.Header = ignored.Header with { SequenceId = 9999 };

        NullableStreamItem response = new()
        {
            IsEndOfStream = true,
            Capacity = 128,
            Duration = null
        };
        response.Header = response.Header with { SequenceId = request.Header.SequenceId };

        FakeStreamSession session = new(ignored, response);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

        List<NullableStreamSnapshot> snapshots = await ReadAllSnapshotsAsync(session, request, cts.Token);

        NullableStreamSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(request.Header.SequenceId, snapshot.SequenceId);
        Assert.Equal(1, NullableStreamItem.DisposeCount);
    }

    [Fact]
    public async Task SubscribeChannelWhenBoundedChannelDropsWriteDisposesDroppedPacket()
    {
        NullableStreamItem.DisposeCount = 0;

        FakeStreamSession session = new();
        ChannelReader<NullableStreamItem> reader = session.SubscribeChannel<NullableStreamItem>(
            boundedCapacity: 1,
            fullMode: BoundedChannelFullMode.DropWrite);

        session.Emit(new NullableStreamItem { IsEndOfStream = false, Capacity = 1 });
        session.Emit(new NullableStreamItem { IsEndOfStream = false, Capacity = 2 });

        NullableStreamItem retained = await reader.ReadAsync();

        Assert.Equal(1, retained.Capacity);
        Assert.Equal(1, NullableStreamItem.DisposeCount);
    }

    [Fact]
    public async Task CollectStreamAsyncWhenExplicitTerminatorArrivesSkipsTerminator()
    {
        NullableStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 1004 };

        NullableStreamItem item = new() { Capacity = 10 };
        item.Header = item.Header with { SequenceId = request.Header.SequenceId };

        NullableStreamItem terminator = new() { IsEndOfStream = true, IsTerminator = true };
        terminator.Header = terminator.Header with { SequenceId = request.Header.SequenceId };

        FakeStreamSession session = new(item, terminator);

        List<NullableStreamItem> items = await session.CollectStreamAsync<NullableStreamItem>(() => request);

        NullableStreamItem result = Assert.Single(items);
        Assert.Equal(10, result.Capacity);
    }

    [Fact]
    public async Task CollectStreamAsyncWhenRetrySucceedsUsesFreshRequest()
    {
        int requestCount = 0;

        FakeStreamSession session = new(attempt =>
        {
            NullableStreamItem item = new() { Capacity = attempt };
            item.Header = item.Header with { SequenceId = (ushort)(2000 + attempt) };

            if (attempt == 1)
            {
                return [item];
            }

            item.IsEndOfStream = true;
            return [item];
        });

        List<NullableStreamItem> items = await session.CollectStreamAsync<NullableStreamItem>(
            () =>
            {
                requestCount++;
                NullableStreamRequest request = new();
                request.Header = request.Header with { SequenceId = (ushort)(2000 + requestCount) };
                return request;
            },
            timeoutMs: 25,
            maxAttempts: 2);

        NullableStreamItem result = Assert.Single(items);
        Assert.Equal(2, requestCount);
        Assert.Equal(2, result.Capacity);
    }

    [Fact]
    public async Task CollectStreamAsyncWhenDuplicateKeyArrivesDisposesDuplicate()
    {
        NullableStreamItem.DisposeCount = 0;

        NullableStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 1005 };

        NullableStreamItem first = new() { Capacity = 7 };
        first.Header = first.Header with { SequenceId = request.Header.SequenceId };

        NullableStreamItem duplicate = new() { Capacity = 7 };
        duplicate.Header = duplicate.Header with { SequenceId = request.Header.SequenceId };

        NullableStreamItem terminal = new() { Capacity = 8, IsEndOfStream = true };
        terminal.Header = terminal.Header with { SequenceId = request.Header.SequenceId };

        FakeStreamSession session = new(first, duplicate, terminal);

        List<NullableStreamItem> items = await session.CollectStreamAsync<NullableStreamItem, int?>(
            () => request,
            item => item.Capacity);

        Assert.Collection(
            items,
            item => Assert.Equal(7, item.Capacity),
            item => Assert.Equal(8, item.Capacity));
        Assert.Equal(1, NullableStreamItem.DisposeCount);
    }

    [Fact]
    public async Task StreamIntoAsyncBatchesIncrementally()
    {
        NullableStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 1006 };

        NullableStreamItem first = new() { Capacity = 1 };
        first.Header = first.Header with { SequenceId = request.Header.SequenceId };

        NullableStreamItem second = new() { Capacity = 2, IsEndOfStream = true };
        second.Header = second.Header with { SequenceId = request.Header.SequenceId };

        FakeStreamSession session = new(first, second);
        List<NullableStreamItem> target = [];
        int callbackCount = 0;

        await session.StreamIntoAsync<NullableStreamItem, int?>(
            () => request,
            target,
            item => item.Capacity,
            () => callbackCount++,
            batchCount: 1);

        Assert.Collection(
            target,
            item => Assert.Equal(1, item.Capacity),
            item => Assert.Equal(2, item.Capacity));
        Assert.Equal(2, callbackCount);
    }

    private static async Task<NullableStreamItem> ReadSingleStreamItemAsync(NullableStreamRequest request, NullableStreamItem response)
    {
        FakeStreamSession session = new(response);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));

        Task<List<NullableStreamSnapshot>> readTask = ReadAllSnapshotsAsync(session, request, cts.Token);
        Task completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(3), cts.Token));

        Assert.Same(readTask, completed);

        List<NullableStreamSnapshot> snapshots = await readTask;
        NullableStreamSnapshot snapshot = Assert.Single(snapshots);

        Assert.Equal(response.Header.SequenceId, snapshot.SequenceId);

        return new NullableStreamItem
        {
            Header = new PacketHeader { SequenceId = snapshot.SequenceId },
            IsEndOfStream = snapshot.IsEndOfStream,
            Capacity = snapshot.Capacity,
            Duration = snapshot.Duration
        };
    }

    private static async Task<List<NullableStreamSnapshot>> ReadAllSnapshotsAsync(
        FakeStreamSession session,
        NullableStreamRequest request,
        CancellationToken cancellationToken)
    {
        List<NullableStreamSnapshot> snapshots = [];

        await foreach (NullableStreamItem item in session.StreamAsync<NullableStreamItem>(request, ct: cancellationToken))
        {
            snapshots.Add(new NullableStreamSnapshot(
                item.Header.SequenceId,
                item.IsEndOfStream,
                item.Capacity,
                item.Duration));
        }

        return snapshots;
    }

    private sealed class FakeStreamSession : TransportSession
    {
        private readonly Func<int, NullableStreamItem[]> _responseFactory;
        private int _sendCount;

        public FakeStreamSession(params NullableStreamItem[] responses) : this(_ => responses)
        {
        }

        public FakeStreamSession(Func<int, NullableStreamItem[]> responseFactory)
            => _responseFactory = responseFactory;

        public override TransportOptions Options { get; } = new();
        public override bool IsConnected => true;

        public override event EventHandler? OnConnected
        {
            add { }
            remove { }
        }

        public override event EventHandler<Exception>? OnDisconnected
        {
            add { }
            remove { }
        }

        public override event EventHandler<IBufferLease>? OnMessageReceived;

        public override event EventHandler<Exception>? OnError
        {
            add { }
            remove { }
        }

        public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task SendAsync(IPacket packet, CancellationToken ct = default)
            => this.SendAsync(packet, encrypt: null, ct);

        public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
        {
            int attempt = Interlocked.Increment(ref _sendCount);

            foreach (NullableStreamItem response in _responseFactory(attempt))
            {
                Emit(response);
            }

            return Task.CompletedTask;
        }

        public void Emit(NullableStreamItem response)
        {
            byte[] data = response.Serialize();
            using BufferLease lease = BufferLease.CopyFrom(data);
            this.OnMessageReceived?.Invoke(this, lease);
        }

        public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override void ResetSequenceCounters() { }

        protected override void Dispose(bool disposing) { }
    }

    private readonly record struct NullableStreamSnapshot(
        ushort SequenceId,
        bool IsEndOfStream,
        int? Capacity,
        TimeSpan? Duration);

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class NullableStreamRequest : PacketBase<NullableStreamRequest>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 0x7A89;
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class NullableStreamItem : PacketBase<NullableStreamItem>, IPacketStaticOpcode, IPacketStreamable, IPacketStreamTerminator
    {
        public static ushort StaticOpCode => 0x7A8A;
        public static int DisposeCount { get; set; }

        [SerializeOrder(0)]
        public bool IsEndOfStream { get; set; }

        [SerializeOrder(1)]
        public int? Capacity { get; set; }

        [SerializeOrder(2)]
        public TimeSpan? Duration { get; set; }

        [SerializeOrder(3)]
        public bool IsTerminator { get; set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}
