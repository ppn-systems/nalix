// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Serialization;
using Nalix.Codec.Tests.TestSupport;

namespace Nalix.Codec.Tests.Serialization;

public sealed class LiteSerializerArrayTests
{
    [Fact]
    public void SerializeDeserialize_IntArray_RoundTripsValues()
    {
        int[] input = [1, 2, 3, 4];
        int[] output = LiteSerializerRoundTrip.RoundTrip(input);

        Assert.Equal(input, output);
    }

    [Fact]
    public void SerializeDeserialize_EmptyArray_RoundTripsValues()
    {
        int[] input = [];
        int[] output = LiteSerializerRoundTrip.RoundTrip(input);

        Assert.Empty(output);
    }

    [Fact]
    public void SerializeDeserialize_NullArray_RoundTripsNull()
    {
        int[]? input = null;
        byte[] buffer = LiteSerializer.Serialize(input);
        int[]? output = new int[1];

        _ = LiteSerializer.Deserialize(buffer, ref output);

        Assert.Null(output);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MaxValue)]
    public void Deserialize_WhenReferenceArrayLengthIsInvalid_ThrowsSerializationFailureException(int length)
    {
        byte[] buffer = BitConverter.GetBytes(length);
        object[]? output = null;

        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(buffer, ref output));
        Assert.Null(output);
    }
}

















