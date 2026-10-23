// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Memory;

namespace Nalix.Environment.Tests.Memory;

public sealed class DataReaderTests
{
    [Theory]
    [InlineData("array")]
    [InlineData("span")]
    [InlineData("memory")]
    public void Advance_WhenReaderHasData_UpdatesBytesReadAndRemaining(string sourceKind)
    {
        byte[] data = [1, 2, 3, 4];
        DataReader reader = sourceKind switch
        {
            "array" => new DataReader(data),
            "span" => new DataReader((ReadOnlySpan<byte>)data),
            "memory" => new DataReader((ReadOnlyMemory<byte>)data),
            _ => throw new InvalidOperationException("Unexpected source kind.")
        };

        ref byte first = ref reader.GetSpanReference(1);
        byte observed = first;
        reader.Advance(2);
        int bytesRead = reader.BytesRead;
        int bytesRemaining = reader.BytesRemaining;
        reader.Dispose();

        Assert.Equal((byte)1, observed);
        Assert.Equal(2, bytesRead);
        Assert.Equal(2, bytesRemaining);
        Assert.Equal(0, reader.BytesRead);
        Assert.Equal(0, reader.BytesRemaining);
    }

    [Fact]
    public void Advance_WhenCountIsOutOfRange_ThrowsExpectedExceptions()
    {
        SerializationFailureException spanException = Assert.ThrowsAny<SerializationFailureException>(() =>
        {
            DataReader reader = new([1, 2]);
            reader.GetSpanReference(3);
        });

        SerializationFailureException advanceException = Assert.ThrowsAny<SerializationFailureException>(() =>
        {
            DataReader reader = new([1, 2]);
            reader.Advance(3);
        });

        ArgumentOutOfRangeException negativeException = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            DataReader reader = new([1, 2]);
            reader.Advance(-1);
        });

        Assert.Contains("end of the stream", spanException.Message);
        Assert.Contains("end of the stream", advanceException.Message);
        Assert.Equal("count", negativeException.ParamName);
    }
}
