// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Memory;

namespace Nalix.Environment.Tests.Memory;

public sealed class BufferLeaseTests
{
    [Fact]
    public void ReleaseOwnership_WhenCalledTwice_ReturnsFalseOnSecondCall()
    {
        using BufferLease lease = BufferLease.CopyFrom([9, 8, 7, 6]);

        bool first = lease.ReleaseOwnership(out byte[]? detached, out int start, out int length);
        bool second = lease.ReleaseOwnership(out _, out _, out _);

        Assert.True(first);
        Assert.NotNull(detached);
        Assert.Equal(0, start);
        Assert.Equal(4, length);
        Assert.False(second);
    }

    [Fact]
    public void CommitLength_WhenSetToCapacity_UsesFullSlice()
    {
        using BufferLease lease = BufferLease.Rent(32);
        lease.CommitLength(lease.Capacity);

        Assert.Equal(lease.Capacity, lease.Length);
        Assert.Equal(lease.Capacity, lease.Span.Length);
    }

    [Fact]
    public void FromRented_WhenBufferIsNull_ThrowsArgumentNullException() => _ = Assert.Throws<ArgumentNullException>(() => BufferLease.FromRented(null!, 0));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyFrom_WhenCalled_ExposesLeaseProperties(bool zeroOnDispose)
    {
        byte[] source = [1, 2, 3, 4];

        using BufferLease lease = BufferLease.CopyFrom(source, zeroOnDispose);

        Assert.Equal(source.Length, lease.Length);
        Assert.True(lease.Capacity >= source.Length);
        Assert.True(lease.RawCapacity >= lease.Capacity);
        Assert.Equal(zeroOnDispose, lease.ZeroOnDispose);
        Assert.Equal(source, lease.Memory.ToArray());
        Assert.Equal(source, lease.Span.ToArray());
        Assert.True(lease.SpanFull.Length >= source.Length);
    }

    [Fact]
    public void ReleaseOwnership_WhenSharedVersusSoleOwner_DetachesOnlySoleOwner()
    {
        using BufferLease sharedLease = BufferLease.CopyFrom([9, 8, 7]);
        using BufferLease ownedLease = BufferLease.TakeOwnership([1, 2, 3, 4], 1, 2);
        sharedLease.Retain();

        bool sharedReleased = sharedLease.ReleaseOwnership(out byte[]? sharedBuffer, out int sharedStart, out int sharedLength);
        sharedLease.Dispose();
        bool ownedReleased = ownedLease.ReleaseOwnership(out byte[]? ownedBuffer, out int ownedStart, out int ownedLength);

        Assert.False(sharedReleased);
        Assert.Null(sharedBuffer);
        Assert.Equal(0, sharedStart);
        Assert.Equal(0, sharedLength);

        Assert.True(ownedReleased);
        Assert.NotNull(ownedBuffer);
        Assert.Equal(1, ownedStart);
        Assert.Equal(2, ownedLength);
        Assert.Equal(0, ownedLease.Length);
        Assert.Equal(0, ownedLease.Capacity);
    }

    [Fact]
    public void CommitLength_WhenLengthExceedsCapacity_ThrowsArgumentOutOfRangeException()
    {
        using BufferLease lease = BufferLease.Rent(8);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => lease.CommitLength(lease.Capacity + 1));
    }

    [Fact]
    public void TakeOwnership_WhenSliceExceedsBufferBounds_ThrowsArgumentOutOfRangeException()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => BufferLease.TakeOwnership([1, 2, 3, 4], 2, 3));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromRented_WithZeroOnDisposeSetting_PreservesPayload(bool zeroOnDispose)
    {
        byte[] wholeBuffer = BufferLease.ByteArrayPool.Rent(4);
        byte[] sliceBuffer = BufferLease.ByteArrayPool.Rent(4);
        byte[] source = [10, 20, 30, 40];
        source.CopyTo(wholeBuffer, 0);
        source.CopyTo(sliceBuffer, 0);

        using BufferLease wholeLease = BufferLease.FromRented(wholeBuffer, 3, zeroOnDispose);
        using BufferLease sliceLease = BufferLease.TakeOwnership(sliceBuffer, 1, 2, zeroOnDispose);

        Assert.Equal([10, 20, 30], wholeLease.Memory.ToArray());
        Assert.Equal([20, 30], sliceLease.Memory.ToArray());
        Assert.Equal(zeroOnDispose, wholeLease.ZeroOnDispose);
        Assert.Equal(zeroOnDispose, sliceLease.ZeroOnDispose);
    }

    // BufferLease.AsSegment is compiled into Debug builds only.
#if DEBUG
    [Fact]
    public void AsSegment_WhenLeaseContainsData_ReturnsMatchingSegment()
    {
        byte[] buffer = BufferLease.ByteArrayPool.Rent(4);
        byte[] source = [4, 5, 6, 7];
        source.CopyTo(buffer, 0);
        using BufferLease lease = BufferLease.TakeOwnership(buffer, 1, 2);

        ArraySegment<byte> segment = lease.AsSegment();

        Assert.Equal(1, segment.Offset);
        Assert.Equal(2, segment.Count);
        Assert.Equal([5, 6], segment.ToArray());
    }
#endif

    [Fact]
    public void ByteArrayPool_WhenRentingStackAllocThreshold_ReturnsUsableArray()
    {
        int requestedCapacity = BufferLease.StackAllocThreshold;
        byte[] buffer = BufferLease.ByteArrayPool.Rent(requestedCapacity);

        Assert.NotNull(buffer);
        Assert.True(buffer.Length >= requestedCapacity);

        BufferLease.ByteArrayPool.Return(buffer);
    }
}
