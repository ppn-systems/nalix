// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Environment.Memory;
using Xunit;

namespace Nalix.Environment.Tests.Memory;

public sealed class Leb128Tests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(127, 1)]
    [InlineData(128, 2)]
    [InlineData(255, 2)]
    [InlineData(25565, 3)]
    [InlineData(2097151, 3)]
    [InlineData(2147483647, 5)]
    [InlineData(-1, 5)]
    public void GetByteCount_WhenValueProvided_ReturnsExpectedSize(int value, int expectedSize)
    {
        Assert.Equal(expectedSize, Leb128.GetByteCount(value));
    }

    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(1, new byte[] { 0x01 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x80, 0x01 })]
    [InlineData(255, new byte[] { 0xFF, 0x01 })]
    [InlineData(25565, new byte[] { 0xDD, 0xC7, 0x01 })]
    [InlineData(2097151, new byte[] { 0xFF, 0xFF, 0x7F })]
    [InlineData(2147483647, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 })]
    [InlineData(-1, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    public void WriteAndTryRead_WhenValueProvided_RoundTrips(int value, byte[] expectedBytes)
    {
        Span<byte> buffer = stackalloc byte[5];
        int written = Leb128.Write(buffer, value);

        Assert.Equal(expectedBytes.Length, written);
        Assert.True(buffer[..written].SequenceEqual(expectedBytes));

        bool success = Leb128.TryRead(buffer[..written], out int readValue, out int bytesRead);

        Assert.True(success);
        Assert.Equal(value, readValue);
        Assert.Equal(written, bytesRead);
    }

    [Fact]
    public void TryRead_WhenInputIsIncomplete_ReturnsFalse()
    {
        ReadOnlySpan<byte> buffer = stackalloc byte[] { 0x80 }; // Needs 1 more byte

        bool success = Leb128.TryRead(buffer, out int value, out int bytesRead);

        Assert.False(success);
        Assert.Equal(0, value);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void TryRead_WhenInputIsOverlong_ThrowsFormatException()
    {
        byte[] buffer = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }; // 6 bytes

        Assert.Throws<FormatException>(() => Leb128.TryRead(buffer, out int _, out int _));
    }
}
