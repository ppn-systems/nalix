// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Xunit;

namespace Nalix.Abstractions.Tests.Primitives;

public sealed class Bytes32Tests
{
    [Fact]
    public void Ctor_WithValidSpan_CopiesData()
    {
        byte[] data = new byte[32];
        for (int i = 0; i < 32; i++) data[i] = (byte)i;

        Bytes32 b = new(data);
        
        Assert.Equal(data, b.ToByteArray());
    }

    [Fact]
    public void Ctor_WithShortSpan_ThrowsArgumentException()
    {
        byte[] data = new byte[31];
        Assert.Throws<ArgumentException>(() => new Bytes32(data));
    }

    [Fact]
    public void Equals_WhenBitwiseIdentical_ReturnsTrue()
    {
        byte[] data = new byte[32];
        new Random(42).NextBytes(data);

        Bytes32 b1 = new(data);
        Bytes32 b2 = new(data);

        Assert.True(b1.Equals(b2));
        Assert.True(b1 == b2);
    }

    [Fact]
    public void Equals_WhenDataDiffers_ReturnsFalse()
    {
        byte[] data1 = new byte[32];
        byte[] data2 = new byte[32];
        data1[31] = 1;
        data2[31] = 2;

        Bytes32 b1 = new(data1);
        Bytes32 b2 = new(data2);

        Assert.False(b1.Equals(b2));
        Assert.True(b1 != b2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    public void Equals_WithBitFlipInByte_ReturnsFalse(int byteIndex)
    {
        byte[] data1 = new byte[32];
        new Random(byteIndex).NextBytes(data1);
        byte[] data2 = (byte[])data1.Clone();
        
        // Flip a bit that is NOT bit 7 (to ensure MoveMask alone would fail)
        data2[byteIndex] ^= 0x01; 

        Bytes32 b1 = new(data1);
        Bytes32 b2 = new(data2);

        Assert.False(b1.Equals(b2), $"Failed to detect difference at byte {byteIndex}");
    }

    [Theory]
    [InlineData(0, 0x80)] // Bit 7 (MoveMask would see this)
    [InlineData(15, 0x40)]
    [InlineData(31, 0x01)] // Bit 0 (MoveMask would MISS this if it was the only check)
    public void Equals_WithSpecificBitMaskInByte_ReturnsFalse(int byteIndex, byte mask)
    {
        byte[] data1 = new byte[32];
        byte[] data2 = new byte[32];
        data2[byteIndex] = mask;

        Bytes32 b1 = new(data1);
        Bytes32 b2 = new(data2);

        Assert.False(b1.Equals(b2), $"Failed to detect bit mask {mask:X2} at byte {byteIndex}");
    }

    [Fact]
    public void IsZero_WhenAnySingleByteIsNonZero_ReturnsFalse()
    {
        for (int i = 0; i < 32; i++)
        {
            byte[] data = new byte[32];
            data[i] = 0x01; // Smallest bit
            Bytes32 b = new(data);
            Assert.False(b.IsZero, $"IsZero returned true for non-zero byte at {i}");
            
            data[i] = 0x80; // Largest bit
            b = new(data);
            Assert.False(b.IsZero, $"IsZero returned true for non-zero byte at {i}");
        }
    }

    [Fact]
    public void IsZero_WhenAllZero_ReturnsTrue()
    {
        Bytes32 b = Bytes32.Zero;
        Assert.True(b.IsZero);
    }

    [Fact]
    public void IsZero_WhenOneBitSet_ReturnsFalse()
    {
        byte[] data = new byte[32];
        data[15] = 1;
        Bytes32 b = new(data);
        Assert.False(b.IsZero);
    }

    [Fact]
    public void Parse_WithValidHexString_RoundTripsToString()
    {
        string hex = "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20";
        Bytes32 b = Bytes32.Parse(hex);
        
        Assert.Equal(hex, b.ToString());
    }

    [Fact]
    public void Parse_WithLowercaseHex_ReturnsUppercaseString()
    {
        string hex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";
        Bytes32 b = Bytes32.Parse(hex);
        Assert.Equal(hex.ToUpperInvariant(), b.ToString());
    }

    [Fact]
    public void Parse_WithUppercaseHex_ReturnsSameString()
    {
        string hex = "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20";
        Bytes32 b = Bytes32.Parse(hex);
        Assert.Equal(hex, b.ToString());
    }

    [Theory]
    [InlineData("0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1Fzz")] // invalid characters zz
    [InlineData("0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F--")] // invalid symbols
    [InlineData("0102030405060708090A0B0C0D0E0F10111213 415161718191A1B1C1D1E1F20")] // whitespace inside body
    [InlineData(" 0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F2")] // odd length (63 chars) with leading space
    [InlineData("0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F200")] // too long (65 chars)
    [InlineData("01020304050607")] // too short
    public void Parse_WithInvalidHex_ThrowsFormatException(string invalidHex)
    {
        Assert.Throws<FormatException>(() => Bytes32.Parse(invalidHex));
    }

    [Fact]
    public void Parse_WithNullHex_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Bytes32.Parse(null!));
    }

    [Fact]
    public void GetHashCode_WhenDataIsEqual_ReturnsSameHashCode()
    {
        byte[] data = new byte[32];
        new Random(42).NextBytes(data);

        Bytes32 b1 = new(data);
        Bytes32 b2 = new(data);

        Assert.Equal(b1.GetHashCode(), b2.GetHashCode());
    }
}
