// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Nalix.Environment.Memory;

namespace Nalix.Environment.Tests.Memory;

/// <summary>
/// Covers the scrub a lease performs when it goes back to the pool carrying secret material.
/// </summary>
public sealed class BufferLeaseZeroOnDisposeTests
{
    private const byte Marker = 0xAB;

    /// <summary>
    /// Fills every byte of the rented array — headroom, payload and the uncommitted tail — and
    /// hands back the array so the caller can inspect it after the lease is disposed.
    /// </summary>
    private static byte[] FillAndRelease(bool zeroOnDispose, int commit)
    {
        BufferLease lease = BufferLease.Rent(64, zeroOnDispose, headroom: 8);

        _ = lease.TryGetSegmentWithHeader(8, out System.ArraySegment<byte> segment).Should().BeTrue();
        byte[] array = segment.Array!;

        System.Array.Fill(array, Marker);
        lease.CommitLength(commit);

        lease.Dispose();
        return array;
    }

    [Fact]
    public void Dispose_WithZeroOnDispose_ClearsBytesPastTheCommittedLength()
    {
        const int headroom = 8;
        const int commit = 16;

        byte[] array = FillAndRelease(zeroOnDispose: true, commit);

        // Everything past the committed payload — where SpanFull lets a caller write before, or
        // without, committing a length — must come back scrubbed.
        _ = array[(headroom + commit)..].Should().AllBeEquivalentTo<byte>(
            0,
            "a lease marked ZeroOnDispose must not leave secret bytes past its committed length");
    }

    [Fact]
    public void Dispose_WithZeroOnDispose_ClearsTheHeaderHeadroom()
    {
        const int headroom = 8;

        byte[] array = FillAndRelease(zeroOnDispose: true, commit: 16);

        _ = array[..headroom].Should().AllBeEquivalentTo<byte>(
            0,
            "the transport header a caller writes into the headroom sits below the committed slice");
    }

    [Fact]
    public void Dispose_WithZeroOnDispose_AndNothingCommitted_ClearsTheWholeArray()
    {
        // A committed length of zero used to skip the scrub entirely, leaving whatever the caller
        // had written through SpanFull in the pool. Nothing is committed here, so the DEBUG poison
        // does not apply either and the whole array must read back as zeroes.
        byte[] array = FillAndRelease(zeroOnDispose: true, commit: 0);

        _ = array.Should().AllBeEquivalentTo<byte>(0);
    }

    [Fact]
    public void Dispose_WithoutZeroOnDispose_LeavesTheArrayAlone()
    {
        byte[] array = FillAndRelease(zeroOnDispose: false, commit: 16);

#if DEBUG
        // DEBUG builds poison the committed slice, so only the untouched tail is asserted here.
        _ = array[^1].Should().Be(Marker);
#else
        _ = array.Should().AllBeEquivalentTo(Marker);
#endif
    }
}
