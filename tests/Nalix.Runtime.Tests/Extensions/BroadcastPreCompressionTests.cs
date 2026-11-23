// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Runtime.Extensions;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Extensions;

/// <summary>
/// A broadcast serializes the packet once and, when it is large enough, compresses it once before it is
/// handed to the hub, instead of compressing it again for every connection.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class BroadcastPreCompressionTests
{
    private const int PayloadSize = 4096;

    [Fact]
    public async Task BroadcastAsync_WithALargePacket_HandsACompressedFrameToTheHub()
    {
        RecordingBroadcaster hub = new();

        await hub.BroadcastAsync(new LargePacket(), enableEncrypt: false);

        AssertCompressed(hub);
    }

    [Fact]
    public async Task MulticastAsync_WithALargePacket_HandsACompressedFrameToTheHub()
    {
        RecordingBroadcaster hub = new();

        await hub.MulticastAsync(new EmptyGroups(), "room", new LargePacket(), enableEncrypt: false);

        AssertCompressed(hub);
    }

    [Fact]
    public async Task MulticastExceptAsync_WithALargePacket_HandsACompressedFrameToTheHub()
    {
        RecordingBroadcaster hub = new();
        using FakeConnection excluded = new();

        await hub.MulticastExceptAsync(new EmptyGroups(), "room", excluded, new LargePacket(), enableEncrypt: false);

        AssertCompressed(hub);
    }

    private static void AssertCompressed(RecordingBroadcaster hub)
    {
        Assert.Equal(1, hub.Calls);
        Assert.InRange(hub.FrameLength, 1, PayloadSize - 1);
    }

    /// <summary>A packet whose payload is all zeros, so it always compresses well.</summary>
    private sealed class LargePacket : IPacket
    {
        public int Length => PacketConstants.HeaderSize + PayloadSize;

        public PacketHeader Header { get; set; }

        public byte[] Serialize() => new byte[this.Length];

        public int Serialize(Span<byte> buffer)
        {
            buffer[..this.Length].Clear();
            return this.Length;
        }
    }

    /// <summary>Records the length of the frame the extension method passes on; it has no connections.</summary>
    private sealed class RecordingBroadcaster : IConnectionBroadcaster
    {
        public int Calls { get; private set; }

        public int FrameLength { get; private set; }

        public Task BroadcastAsync<TState, TSender>(TState state, TSender sender, CancellationToken cancellationToken = default)
            where TSender : struct, IConnectionSender<TState> => this.Record(state);

        public Task MulticastAsync<TState, TSender>(IConnectionGroupRegistry groupProvider, string groupName, TState state, TSender sender, CancellationToken cancellationToken = default)
            where TSender : struct, IConnectionSender<TState> => this.Record(state);

        public Task MulticastExceptAsync<TState, TSender>(IConnectionGroupRegistry groupProvider, string groupName, IConnection excludedConnection, TState state, TSender sender, CancellationToken cancellationToken = default)
            where TSender : struct, IConnectionSender<TState> => this.Record(state);

        public Task BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MulticastAsync(IConnectionGroupRegistry groupProvider, string groupName, ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MulticastExceptAsync(IConnectionGroupRegistry groupProvider, string groupName, IConnection excludedConnection, ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task BroadcastWhereAsync(ReadOnlyMemory<byte> message, Func<IConnection, bool> predicate, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private Task Record<TState>(TState state)
        {
            // The state type is private to the extension class; its RawLease field is the frame to send.
            IBufferLease frame = (IBufferLease)typeof(TState).GetField("RawLease")!.GetValue(state)!;
            this.Calls++;
            this.FrameLength = frame.Length;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyGroups : IConnectionGroupRegistry
    {
        public Task AddToGroupAsync(string groupName, IConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string groupName, IConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromAllGroupsAsync(IConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyCollection<IConnection> GetGroupMembers(string groupName) => [];
    }
}
