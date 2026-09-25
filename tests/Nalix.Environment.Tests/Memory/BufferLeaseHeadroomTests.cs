// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Nalix.Environment.Memory;

namespace Nalix.Environment.Tests.Memory;

public sealed class BufferLeaseHeadroomTests
{
    [Fact]
    public void Rent_WithHeadroom_PayloadStartsAfterReservedBytes()
    {
        using BufferLease lease = BufferLease.Rent(16, zeroOnDispose: false, headroom: 2);

        _ = lease.Headroom.Should().Be(2);
        _ = lease.Capacity.Should().BeGreaterThanOrEqualTo(16);
        _ = lease.RawCapacity.Should().Be(lease.Capacity + 2);

        byte[] payload = [1, 2, 3, 4, 5];
        payload.CopyTo(lease.SpanFull);
        lease.CommitLength(payload.Length);

        _ = lease.Span.ToArray().Should().Equal(payload);
        _ = lease.Memory.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void TryGetSegmentWithHeader_ReturnsHeaderPlusPayload_WithoutTouchingPayload()
    {
        using BufferLease lease = BufferLease.Rent(8, zeroOnDispose: false, headroom: BufferLease.TransportHeadroom);
        byte[] payload = [9, 8, 7];
        payload.CopyTo(lease.SpanFull);
        lease.CommitLength(payload.Length);

        bool ok = lease.TryGetSegmentWithHeader(2, out ArraySegment<byte> segment);

        _ = ok.Should().BeTrue();
        _ = segment.Count.Should().Be(payload.Length + 2);
        segment.AsSpan(0, 2).Fill(0xAB);

        _ = segment.AsSpan(2).ToArray().Should().Equal(payload);
        _ = lease.Span.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void TryGetSegmentWithHeader_FailsWithoutReservedHeadroom()
    {
        using BufferLease plain = BufferLease.Rent(8);
        plain.CommitLength(4);
        _ = plain.Headroom.Should().Be(0);
        _ = plain.TryGetSegmentWithHeader(2, out _).Should().BeFalse();

        // A slice that merely starts at an offset (e.g. a received payload after a header) is not
        // reserved headroom: the prefix may still be meaningful to its owner.
        byte[] array = BufferLease.ByteArrayPool.Rent(16);
        using BufferLease sliced = BufferLease.TakeOwnership(array, start: 4, length: 4);
        _ = sliced.Headroom.Should().Be(0);
        _ = sliced.TryGetSegmentWithHeader(2, out _).Should().BeFalse();

        using BufferLease small = BufferLease.Rent(8, zeroOnDispose: false, headroom: 1);
        small.CommitLength(1);
        _ = small.TryGetSegmentWithHeader(2, out _).Should().BeFalse();
    }

    [Fact]
    public void Dispose_WithZeroOnDispose_ScrubsUsedRange()
    {
        BufferLease lease = BufferLease.Rent(32, zeroOnDispose: true, headroom: 2);
        byte[] secret = [0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A];
        secret.CopyTo(lease.SpanFull);
        lease.CommitLength(secret.Length);

        _ = lease.TryGetSegmentWithHeader(2, out ArraySegment<byte> segment).Should().BeTrue();
        byte[] backing = segment.Array!;
        int payloadOffset = segment.Offset + 2;

        lease.Dispose();

        // Release builds leave zeros; Debug builds additionally poison-fill. Either way the secret is gone.
        _ = backing.AsSpan(payloadOffset, secret.Length).ToArray().Should().NotContain(0x5A);
    }

    [Fact]
    public void Rent_WithNegativeHeadroom_Throws()
    {
        Action act = () => BufferLease.Rent(8, zeroOnDispose: false, headroom: -1);
        _ = act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
