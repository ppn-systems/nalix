#if DEBUG
// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Codec.Security.Symmetric;
using Xunit;

namespace Nalix.Framework.Tests.Cryptography;

/// <summary>
/// Guards that the vectorized keystream paths stay byte-identical to the scalar one, so a machine
/// without AVX2 (or without any SIMD at all) decrypts what a machine with it encrypted.
/// </summary>
public sealed class ChaCha20CoreTests
{
    private static uint[] StateFrom(int seed)
    {
        uint[] state = new uint[ChaCha20.StateLength];

        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;

        // Deterministic, avalanche-ish filler so every word differs between seeds.
        for (int i = 4; i < state.Length; i++)
        {
            state[i] = (uint)((seed * 0x9E3779B1u) + (i * 0x85EBCA6Bu));
        }

        return state;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(0x1234)]
    public void KeyStreamOneBlockMatchesKeyStreamScalar(int seed)
    {
        uint[] state = StateFrom(seed);

        byte[] vector = new byte[ChaCha20.BlockSize];
        byte[] scalar = new byte[ChaCha20.BlockSize];

        ChaCha20Core.KeyStreamOneBlock(state, vector);
        ChaCha20Core.KeyStreamScalar(state, scalar);

        Assert.Equal(scalar, vector);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(0x1234)]
    public void KeyStreamTwoBlocksMatchesKeyStreamScalarForBothCounters(int seed)
    {
        if (!ChaCha20Core.TwoBlockPathSupported)
        {
            return;
        }

        uint[] state = StateFrom(seed);

        byte[] pair = new byte[2 * ChaCha20.BlockSize];
        ChaCha20Core.KeyStreamTwoBlocks(state, pair);

        byte[] first = new byte[ChaCha20.BlockSize];
        ChaCha20Core.KeyStreamScalar(state, first);

        state[12]++;
        byte[] second = new byte[ChaCha20.BlockSize];
        ChaCha20Core.KeyStreamScalar(state, second);

        Assert.Equal(first, pair[..ChaCha20.BlockSize]);
        Assert.Equal(second, pair[ChaCha20.BlockSize..]);
    }

    [Fact]
    public void KeyStreamPathsDoNotMutateTheState()
    {
        uint[] state = StateFrom(3);
        uint[] expected = (uint[])state.Clone();

        ChaCha20Core.KeyStreamScalar(state, new byte[ChaCha20.BlockSize]);
        Assert.Equal(expected, state);

        ChaCha20Core.KeyStreamOneBlock(state, new byte[ChaCha20.BlockSize]);
        Assert.Equal(expected, state);

        if (ChaCha20Core.TwoBlockPathSupported)
        {
            ChaCha20Core.KeyStreamTwoBlocks(state, new byte[2 * ChaCha20.BlockSize]);
            Assert.Equal(expected, state);
        }
    }

    [Fact]
    public void XorMergeMatchesByteWiseXorForEveryLength()
    {
        byte[] src = new byte[200];
        byte[] keystream = new byte[200];

        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (byte)(i * 3);
            keystream[i] = (byte)(0xA5 ^ i);
        }

        for (int count = 0; count <= src.Length; count++)
        {
            byte[] actual = new byte[src.Length];
            ChaCha20Core.Xor(src, keystream, actual, count);

            for (int i = 0; i < count; i++)
            {
                Assert.Equal((byte)(src[i] ^ keystream[i]), actual[i]);
            }

            for (int i = count; i < actual.Length; i++)
            {
                Assert.Equal(0, actual[i]);
            }
        }
    }
}
#endif
