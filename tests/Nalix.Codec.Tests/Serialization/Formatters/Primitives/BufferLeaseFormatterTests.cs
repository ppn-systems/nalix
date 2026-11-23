// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.Serialization;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Serialization.Formatters.Primitives;

public sealed class BufferLeaseFormatterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(1024)]
    public void RoundTrip_CopiesThePayloadIntoANewLease(int size)
    {
        byte[] payload = new byte[size];
        new Random(size).NextBytes(payload);
        using BufferLease source = BufferLease.CopyFrom(payload);

        byte[] wire = LiteSerializer.Serialize<BufferLease?>(source);
        BufferLease? result = LiteSerializer.Deserialize<BufferLease?>(wire, out _);

        Assert.NotNull(result);
        using (result)
        {
            Assert.NotSame(source, result);
            Assert.Equal(payload, result.Span.ToArray());
        }
    }

    [Fact]
    public void RoundTrip_PreservesNull()
    {
        byte[] wire = LiteSerializer.Serialize<BufferLease?>(null);

        Assert.Null(LiteSerializer.Deserialize<BufferLease?>(wire, out _));
    }

    [Fact]
    public void Wire_IsALengthPrefixFollowedByTheRawBytes()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        using BufferLease source = BufferLease.CopyFrom(payload);

        byte[] wire = LiteSerializer.Serialize<BufferLease?>(source);

        Assert.Equal([5, 0, 0, 0, 1, 2, 3, 4, 5], wire);
    }

    [Fact]
    public void Deserialize_WhenTheLengthExceedsTheInput_ThrowsInsteadOfReadingPastTheBuffer()
    {
        byte[] truncated = [100, 0, 0, 0, 1, 2];

        Assert.ThrowsAny<Exception>(() => LiteSerializer.Deserialize<BufferLease?>(truncated, out _));
    }
}
