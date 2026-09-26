// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
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
public sealed partial class HeaderStreamExtensionsTests
{
    public HeaderStreamExtensionsTests()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task RequestWithStreamAsync_OneStream_StampsRequestAndCorrelatesReplies()
    {
        HeaderStreamRequest request = new();
        Assert.Equal(0, request.Header.SequenceId);

        HeaderStreamDetail header = new() { Total = 2 };
        HeaderStreamItem itemA = new() { Value = 1 };
        HeaderStreamItem itemB = new() { Value = 2, IsEndOfStream = true };

        FakeHeaderStreamSession session = new(attempt =>
        {
            ushort seq = 1; // first stamp on a fresh session's counter
            header.Header = header.Header with { SequenceId = seq };
            itemA.Header = itemA.Header with { SequenceId = seq };
            itemB.Header = itemB.Header with { SequenceId = seq };
            return [header, itemA, itemB];
        });

        (HeaderStreamDetail resultHeader, List<HeaderStreamItem> items) = await session
            .RequestWithStreamAsync<HeaderStreamDetail, HeaderStreamItem>(request, static i => i.Value != 0);

        Assert.Equal(1, request.Header.SequenceId);
        Assert.Equal(2, resultHeader.Total);
        Assert.Collection(items,
            i => Assert.Equal(1, i.Value),
            i => Assert.Equal(2, i.Value));
    }

    [Fact]
    public async Task RequestWithStreamAsync_OneStream_FiltersSentinelItems()
    {
        HeaderStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 5 };

        HeaderStreamDetail header = new() { Total = 0 };
        header.Header = header.Header with { SequenceId = 5 };
        HeaderStreamItem sentinel = new() { Value = 0, IsEndOfStream = true };
        sentinel.Header = sentinel.Header with { SequenceId = 5 };

        FakeHeaderStreamSession session = new(_ => [header, sentinel]);

        (HeaderStreamDetail _, List<HeaderStreamItem> items) = await session
            .RequestWithStreamAsync<HeaderStreamDetail, HeaderStreamItem>(request, static i => i.Value != 0);

        Assert.Empty(items);
    }

    [Fact]
    public async Task RequestWithStreamAsync_TwoStreams_PopulatesBothIndependently()
    {
        HeaderStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 7 };

        HeaderStreamDetail header = new() { Total = 1 };
        header.Header = header.Header with { SequenceId = 7 };
        HeaderStreamItem item1 = new() { Value = 10, IsEndOfStream = true };
        item1.Header = item1.Header with { SequenceId = 7 };
        HeaderStreamOtherItem item2A = new() { Tag = "a" };
        item2A.Header = item2A.Header with { SequenceId = 7 };
        HeaderStreamOtherItem item2B = new() { Tag = "b", IsEndOfStream = true };
        item2B.Header = item2B.Header with { SequenceId = 7 };

        FakeHeaderStreamSession session = new(_ => [header, item1, item2A, item2B]);

        (HeaderStreamDetail _, List<HeaderStreamItem> items1, List<HeaderStreamOtherItem> items2) = await session
            .RequestWithStreamAsync<HeaderStreamDetail, HeaderStreamItem, HeaderStreamOtherItem>(
                request, static i => i.Value != 0, static i => !string.IsNullOrEmpty(i.Tag));

        NullableAssert(items1, items2);

        static void NullableAssert(List<HeaderStreamItem> items1, List<HeaderStreamOtherItem> items2)
        {
            HeaderStreamItem only1 = Assert.Single(items1);
            Assert.Equal(10, only1.Value);
            Assert.Collection(items2,
                i => Assert.Equal("a", i.Tag),
                i => Assert.Equal("b", i.Tag));
        }
    }

    [Fact]
    public async Task RequestWithStreamAsync_WhenSendFails_HeaderTaskFaultsInsteadOfHanging()
    {
        HeaderStreamRequest request = new();
        request.Header = request.Header with { SequenceId = 9 };

        FailingHeaderStreamSession session = new();

        await Assert.ThrowsAsync<Nalix.Abstractions.Exceptions.NetworkException>(
            () => session.RequestWithStreamAsync<HeaderStreamDetail, HeaderStreamItem>(request, static _ => true));
    }

    private sealed class FakeHeaderStreamSession(Func<int, IPacket[]> responseFactory) : TransportSession
    {
        private int _sendCount;

        public override TransportOptions Options { get; } = new();
        public override bool IsConnected => true;

        public override event EventHandler? OnConnected { add { } remove { } }
        public override event EventHandler<Exception>? OnDisconnected { add { } remove { } }
        public override event EventHandler<IBufferLease>? OnMessageReceived;
        public override event EventHandler<Exception>? OnError { add { } remove { } }

        public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task SendAsync(IPacket packet, CancellationToken ct = default)
            => this.SendAsync(packet, encrypt: null, ct);

        public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
        {
            int attempt = Interlocked.Increment(ref _sendCount);
            foreach (IPacket response in responseFactory(attempt))
            {
                byte[] data = response.Serialize();
                using BufferLease lease = BufferLease.CopyFrom(data);
                this.OnMessageReceived?.Invoke(this, lease);
            }

            return Task.CompletedTask;
        }

        public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override void ResetSequenceCounters() { }

        protected override void Dispose(bool disposing) { }
    }

    private sealed class FailingHeaderStreamSession : TransportSession
    {
        public override TransportOptions Options { get; } = new();
        public override bool IsConnected => true;

        public override event EventHandler? OnConnected { add { } remove { } }
        public override event EventHandler<Exception>? OnDisconnected { add { } remove { } }
        public override event EventHandler<IBufferLease>? OnMessageReceived { add { } remove { } }
        public override event EventHandler<Exception>? OnError { add { } remove { } }

        public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task SendAsync(IPacket packet, CancellationToken ct = default)
            => this.SendAsync(packet, encrypt: null, ct);

        public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated send failure");

        public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override void ResetSequenceCounters() { }

        protected override void Dispose(bool disposing) { }
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class HeaderStreamRequest : PacketBase<HeaderStreamRequest>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 0x7B10;
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class HeaderStreamDetail : PacketBase<HeaderStreamDetail>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 0x7B11;

        [SerializeOrder(0)]
        public int Total { get; set; }
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class HeaderStreamItem : PacketBase<HeaderStreamItem>, IPacketStaticOpcode, IPacketStreamable
    {
        public static ushort StaticOpCode => 0x7B12;

        [SerializeOrder(0)]
        public int Value { get; set; }

        [SerializeOrder(1)]
        public bool IsEndOfStream { get; set; }
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class HeaderStreamOtherItem : PacketBase<HeaderStreamOtherItem>, IPacketStaticOpcode, IPacketStreamable
    {
        public static ushort StaticOpCode => 0x7B13;

        [SerializeOrder(0)]
        public string Tag { get; set; } = string.Empty;

        [SerializeOrder(1)]
        public bool IsEndOfStream { get; set; }
    }
}
