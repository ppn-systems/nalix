// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Codec.Tests.TestSupport;
using Nalix.Codec.Transforms;
using Nalix.Environment.Extensions;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Random;
using System.Text;

namespace Nalix.Codec.Tests.DataFrames;

public sealed class FragmentAssemblerTests
{
    [Fact]
    public void Add_WhenAllChunksArriveInOrder_ReturnsAssembledBuffer()
    {
        using FragmentAssembler assembler = new();
        FragmentHeader first = new(7, 0, 2, false);
        FragmentHeader second = new(7, 1, 2, true);

        FragmentAssemblyResult? firstAssembled = assembler.Add(first, Encoding.UTF8.GetBytes("hello "), out bool firstEvicted);
        FragmentAssemblyResult? secondAssembled = assembler.Add(second, Encoding.UTF8.GetBytes("world"), out bool secondEvicted);

        Assert.Null(firstAssembled);
        Assert.False(firstEvicted);
        Assert.False(secondEvicted);
        Assert.True(secondAssembled.HasValue);
        using BufferLease assembled = secondAssembled.Value.Lease;
        Assert.Equal("hello world", Encoding.UTF8.GetString(secondAssembled.Value.Span));
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void Add_WhenFirstChunkIsNotChunkZero_ThrowsInvalidDataException()
    {
        using FragmentAssembler assembler = new();
        FragmentHeader second = new(9, 1, 2, true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            assembler.Add(second, Encoding.UTF8.GetBytes("world"), out _));

        Assert.Contains("started with chunk index 1 instead of 0", exception.Message);
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void Add_WhenSequenceStartsAtChunkZeroAndCompletes_AssemblesPayload()
    {
        using FragmentAssembler assembler = new();
        FragmentHeader first = new(10, 0, 2, false);
        FragmentHeader second = new(10, 1, 2, true);

        FragmentAssemblyResult? firstAttempt = assembler.Add(first, Encoding.UTF8.GetBytes("hello "), out bool firstEvicted);
        FragmentAssemblyResult? completed = assembler.Add(second, Encoding.UTF8.GetBytes("world"), out bool secondEvicted);

        Assert.Null(firstAttempt);
        Assert.False(firstEvicted);
        Assert.False(secondEvicted);
        Assert.True(completed.HasValue);
        using BufferLease assembled = completed.Value.Lease;
        Assert.Equal("hello world", Encoding.UTF8.GetString(completed.Value.Span));
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Theory]
    [InlineData((ushort)0, (ushort)0, (ushort)1)]
    [InlineData((ushort)1, (ushort)1, (ushort)1)]
    [InlineData((ushort)1, (ushort)0, (ushort)0)]
    public void Add_WhenHeaderIsInvalid_ThrowsInvalidDataException(ushort streamId, ushort chunkIndex, ushort totalChunks)
    {
        using FragmentAssembler assembler = new();
        FragmentHeader header = new(streamId, chunkIndex, totalChunks, false);

        _ = Assert.Throws<InvalidDataException>(() => assembler.Add(header, [1, 2, 3], out _));
    }

    [Fact]
    public void Add_WhenStreamHasTimedOut_EvictsStreamAndReturnsNull()
    {
        using FragmentAssembler assembler = new() { StreamTimeoutMs = 1000 };
        FragmentHeader first = new(15, 0, 2, false);
        FragmentHeader second = new(15, 1, 2, true);
        _ = assembler.Add(first, [1], out _);
        TickClock.Advance(1500);

        FragmentAssemblyResult? assembled = assembler.Add(second, [2], out bool streamEvicted);

        Assert.Null(assembled);
        Assert.True(streamEvicted);
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void EvictExpiredAndClear_WhenStreamsExist_RemovesTrackedStreams()
    {
        using FragmentAssembler assembler = new() { StreamTimeoutMs = 1000 };
        _ = assembler.Add(new FragmentHeader(21, 0, 2, false), [1, 2], out _);
        TickClock.Advance(1500);

        int evicted = assembler.EvictExpired();
        _ = assembler.Add(new FragmentHeader(22, 0, 2, false), [3, 4], out _);
        assembler.Clear();

        Assert.Equal(1, evicted);
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void Add_WhenEvictIntervalIsReached_SweepsExpiredStreams()
    {
        using FragmentAssembler assembler = new() { StreamTimeoutMs = 2000 };

        _ = assembler.Add(new FragmentHeader(40, 0, 2, false), [1], out _);
        TickClock.Advance(2500);

        for (ushort streamId = 41; streamId < 41 + FragmentAssembler.EvictInterval - 1; streamId++)
        {
            _ = assembler.Add(new FragmentHeader(streamId, 0, 2, false), [2], out _);
        }

        Assert.Equal(FragmentAssembler.EvictInterval - 1, assembler.OpenStreamCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsFragmentedFrame_WhenPayloadVaries_ReturnsExpectedResult(bool useValidPayload)
    {
        byte[] payload = useValidPayload
            ? FrameTestData.CreateFragmentPayload(new FragmentHeader(30, 0, 1, true), [9, 8, 7])
            : [0x01, 0x02, 0x03];

        bool isFragment = FragmentAssembler.IsFragmentedFrame(payload, out FragmentHeader header);

        Assert.Equal(useValidPayload, isFragment);
        if (useValidPayload)
        {
            Assert.Equal((ushort)30, header.StreamId);
            Assert.True(header.IsLast);
        }
        else
        {
            Assert.Equal(default, header);
        }
    }

    [Fact]
    public void Add_WhenSmallFirstChunkClaimsMaxTotalChunks_BoundsInitialAllocation()
    {
        using FragmentAssembler assembler = new();
        FragmentHeader header = new(streamId: 77, chunkIndex: 0, totalChunks: ushort.MaxValue, isLast: false);
        byte[] smallChunk = new byte[257]; // 257 * 65535 = 16,842,495 (> 16 MB MaxStreamBytes)

        FragmentAssemblyResult? result = assembler.Add(header, smallChunk, out bool evicted);

        Assert.Null(result);
        Assert.False(evicted);
        Assert.Equal(1, assembler.OpenStreamCount);

        // Reflect into _streams[77].AccumLease.Capacity to prove initial pre-allocation is bounded (~4 KB, not 16 MB)
        System.Reflection.FieldInfo? streamsField = typeof(FragmentAssembler).GetField("_streams", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(streamsField);
        System.Collections.IDictionary streams = (System.Collections.IDictionary)streamsField!.GetValue(assembler)!;
        object? state = streams[(ushort)77];
        Assert.NotNull(state);
        System.Reflection.FieldInfo? leaseField = state!.GetType().GetField("AccumLease", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(leaseField);
        BufferLease lease = (BufferLease)leaseField!.GetValue(state)!;
        Assert.True(lease.Capacity <= 8192, $"Expected bounded initial capacity <= 8192 bytes, but rented {lease.Capacity} bytes.");
    }

    [Fact]
    public void Add_WhenChunkZeroIsDuplicated_EvictsStreamAndThrows()
    {
        using FragmentAssembler assembler = new();
        FragmentHeader chunk0 = new(streamId: 88, chunkIndex: 0, totalChunks: 3, isLast: false);
        _ = assembler.Add(chunk0, [1, 2, 3, 4], out _);
        Assert.Equal(1, assembler.OpenStreamCount);

        bool evicted = false;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            assembler.Add(chunk0, [5, 6, 7, 8], out evicted));

        Assert.True(evicted);
        Assert.Contains("out-of-order", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void Add_WhenMaxOpenStreamsReachedWithExpiredStreams_SweepsBeforeRejecting()
    {
        using FragmentAssembler assembler = new()
        {
            MaxOpenStreams = 2,
            StreamTimeoutMs = 50
        };

        _ = assembler.Add(new FragmentHeader(streamId: 1, chunkIndex: 0, totalChunks: 2, isLast: false), [1], out _);
        _ = assembler.Add(new FragmentHeader(streamId: 2, chunkIndex: 0, totalChunks: 2, isLast: false), [2], out _);
        Assert.Equal(2, assembler.OpenStreamCount);

        TickClock.Advance(100);

        FragmentAssemblyResult? res = assembler.Add(new FragmentHeader(streamId: 3, chunkIndex: 0, totalChunks: 2, isLast: false), [3], out bool evicted);

        Assert.Null(res);
        Assert.False(evicted);
        Assert.Equal(1, assembler.OpenStreamCount);
    }

    [Fact]
    public void Add_WhenTotalChunksChanges_EvictsStreamAndThrows()
    {
        using FragmentAssembler assembler = new();
        _ = assembler.Add(new FragmentHeader(50, 0, 2, false), [1], out _);

        bool evicted = false;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            assembler.Add(new FragmentHeader(50, 1, 3, true), [2], out evicted));

        Assert.True(evicted);
        Assert.Contains("changed TotalChunks", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void Add_WhenSecondChunkSkipsAhead_EvictsStreamAndThrows()
    {
        using FragmentAssembler assembler = new();
        _ = assembler.Add(new FragmentHeader(51, 0, 3, false), [1], out _);

        bool evicted = false;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            assembler.Add(new FragmentHeader(51, 2, 3, true), [2], out evicted));

        Assert.True(evicted);
        Assert.Contains("out-of-order", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void Add_WhenMaxOpenStreamsReached_ReturnsNullAndMarksEvicted()
    {
        using FragmentAssembler assembler = new() { MaxOpenStreams = 1 };
        _ = assembler.Add(new FragmentHeader(60, 0, 2, false), [1], out _);

        FragmentAssemblyResult? result = assembler.Add(new FragmentHeader(61, 0, 2, false), [2], out bool evicted);

        Assert.Null(result);
        Assert.True(evicted);
        Assert.Equal(1, assembler.OpenStreamCount);
    }
}
