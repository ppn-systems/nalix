// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.LZ4;

namespace Nalix.Codec.Tests.LZ4;

public sealed class LZ4BlockEncoderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(4096)]
    public void GetMinOutputBufferSize_WhenInputLengthVaries_MatchesExpectedFormula(int inputLength)
    {
        int expected = inputLength + (inputLength / 255) + 16;
        int actual = LZ4BlockEncoder.GetMinOutputBufferSize(inputLength);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(4096)]
    public void GetMaxLength_WhenInputLengthVaries_AddsHeaderSizeToMinOutput(int inputLength)
    {
        int minOutput = LZ4BlockEncoder.GetMinOutputBufferSize(inputLength);
        int maxLength = LZ4BlockEncoder.GetMaxLength(inputLength);

        Assert.Equal(minOutput + LZ4BlockHeader.Size, maxLength);
    }

    [Fact]
    public void GetMaxLength_WhenInputLengthIncreases_IsMonotonic()
    {
        int prev = LZ4BlockEncoder.GetMaxLength(0);
        for (int i = 1; i <= 10_000; i += 137)
        {
            int current = LZ4BlockEncoder.GetMaxLength(i);
            Assert.True(current >= prev);
            prev = current;
        }
    }
}


















