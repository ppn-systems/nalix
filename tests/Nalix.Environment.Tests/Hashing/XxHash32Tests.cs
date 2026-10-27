// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Numerics;
using Nalix.Environment.Hashing;

namespace Nalix.Environment.Tests.Hashing;

public sealed class XxHash32Tests
{
    [Fact]
    public void Compute_WhenInputIsEmpty_MatchesKnownVector()
    {
        Assert.Equal(0x02CC5D05u, XxHash32.Compute([], 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(1024)]
    public void Compute_WhenLengthSpansStripes_MatchesReferenceImplementation(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31) + 7);
        }

        foreach (uint seed in new uint[] { 0u, 0x9E3779B1u })
        {
            Assert.Equal(Reference(data, seed), XxHash32.Compute(data, seed));
        }
    }

    // Straightforward scalar xxHash32 per the specification.
    private static uint Reference(byte[] d, uint seed)
    {
        const uint P1 = 0x9E3779B1u, P2 = 0x85EBCA77u, P3 = 0xC2B2AE3Du, P4 = 0x27D4EB2Fu, P5 = 0x165667B1u;
        int len = d.Length, i = 0;
        uint h;

        static uint R(uint acc, uint input)
            => BitOperations.RotateLeft(acc + (input * 0x85EBCA77u), 13) * 0x9E3779B1u;

        if (len >= 16)
        {
            uint v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            while (i <= len - 16)
            {
                v1 = R(v1, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i)));
                v2 = R(v2, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i + 4)));
                v3 = R(v3, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i + 8)));
                v4 = R(v4, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i + 12)));
                i += 16;
            }

            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) +
                BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
        }
        else
        {
            h = seed + P5;
        }

        h += (uint)len;
        while (i + 4 <= len)
        {
            h += BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i)) * P3;
            h = BitOperations.RotateLeft(h, 17) * P4;
            i += 4;
        }

        while (i < len)
        {
            h += d[i++] * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
        }

        h ^= h >> 15;
        h *= P2;
        h ^= h >> 13;
        h *= P3;
        h ^= h >> 16;
        return h;
    }
}
