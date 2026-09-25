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
        byte[] array = FillAndRelease(zeroOnDispose: true, commit: 16);

        _ = array.Should().AllBeEquivalentTo<byte>(
            0,
            "a lease marked ZeroOnDispose must not leave secret bytes anywhere in the pooled array");
    }

    [Fact]
    public void Dispose_WithZeroOnDispose_ClearsTheHeaderHeadroom()
    {
        // A committed length of zero used to skip the scrub entirely, leaving whatever the caller
        // had written into the transport headroom in the pool.
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
