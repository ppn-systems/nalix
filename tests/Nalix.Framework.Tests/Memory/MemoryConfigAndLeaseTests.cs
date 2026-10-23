using Nalix.Environment.Memory;

using System;
using Nalix.Abstractions.Exceptions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Memory;

/// <summary>
/// Covers configuration and buffer-lease APIs in the Memory folder.
/// </summary>
[Trait("Category", "Memory")]
public sealed partial class MemoryTests
{
    [Fact]
    public void Validate_ValidBufferOptions_CompletesSuccessfully()
    {
        BufferOptions config = new()
        {
            EnableBufferLeakDetection = true,
            EnableBufferLeakStackTrace = true,
            SuspiciousThresholdSeconds = 100
        };

        Exception? exception = Record.Exception(config.Validate);

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Validate_InvalidBufferOptions_ThrowsValidationException(int suspiciousThreshold)
    {
        BufferOptions config = new()
        {
            SuspiciousThresholdSeconds = suspiciousThreshold
        };

        _ = Assert.Throws<ValidationException>(config.Validate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyFrom_StateUnderTest_ExposesLeaseProperties(bool zeroOnDispose)
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
    public void ReleaseOwnership_StateUnderTest_ReturnsExpectedOutcome()
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
    public void CommitLength_LengthExceedsCapacity_ThrowsArgumentOutOfRangeException()
    {
        using BufferLease lease = BufferLease.Rent(8);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => lease.CommitLength(lease.Capacity + 1));
    }

    [Fact]
    public void TakeOwnership_SliceExceedsBufferBounds_ThrowsArgumentOutOfRangeException()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => BufferLease.TakeOwnership([1, 2, 3, 4], 2, 3));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromRented_StateUnderTest_PreservesPayload(bool zeroOnDispose)
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
    public void AsSegment_LeaseContainsData_ReturnsMatchingSegment()
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
    public void TestBufferLeaseRent()
    {
        // Rent through the manager directly instead of mutating the process-wide
        // ByteArrayPool delegates.  The original code called
        // BufferLease.ByteArrayPool.Configure(manager) which left a disposed
        // delegate target in the global static field after this method returned.
        using var manager = new BufferPoolManager();
        byte[] arr = manager.Rent(2114);
        try
        {
            Assert.True(arr.Length >= 2114, $"Expected >= 2114, got {arr.Length}");
        }
        finally
        {
            manager.Return(arr);
        }
    }


}













