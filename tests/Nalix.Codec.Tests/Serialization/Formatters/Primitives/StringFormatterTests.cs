// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.Serialization.Formatters.Primitives;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Serialization.Formatters.Primitives;

public sealed class StringFormatterTests
{
    [Fact]
    public void SerializeDeserialize_WhenValueIsNull_RoundTripsNull()
    {
        StringFormatter formatter = new();
        DataWriter writer = new(8);
        try
        {
            formatter.Serialize(ref writer, null!);
            byte[] encoded = writer.ToArray();

            DataReader reader = new(encoded);
            string decoded = formatter.Deserialize(ref reader);

            Assert.Null(decoded);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("xin chao 👋 nalix")]
    public void SerializeDeserialize_WhenValueVaries_RoundTripsString(string input)
    {
        StringFormatter formatter = new();
        DataWriter writer = new(4); // small buffer to also exercise Expand
        try
        {
            formatter.Serialize(ref writer, input);
            byte[] encoded = writer.ToArray();

            DataReader reader = new(encoded);
            string decoded = formatter.Deserialize(ref reader);

            Assert.Equal(input, decoded);
            Assert.Equal(0, reader.BytesRemaining);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void Serialize_WhenUtf8LengthExceedsMax_ThrowsSerializationFailureException()
    {
        StringFormatter formatter = new();
        DataWriter writer = new(16);

        try
        {
            // ASCII: byte count == char count.
            string tooLong = new('a', SerializerBounds.MaxString + 1);
            Exception? ex = null;
            try
            {
                formatter.Serialize(ref writer, tooLong);
            }
            catch (Exception e)
            {
                ex = e;
            }

            _ = Assert.IsType<SerializationFailureException>(ex, exactMatch: false);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void Deserialize_WhenLengthIsNegativeAndNotNullSentinel_ReturnsNullAndPoisonsReader()
    {
        StringFormatter formatter = new();
        byte[] invalid = BitConverter.GetBytes(-2);
        DataReader reader = new(invalid);

        string result = formatter.Deserialize(ref reader);

        Assert.True(reader.IsFailed);
        Assert.Null(result);
    }
}



















