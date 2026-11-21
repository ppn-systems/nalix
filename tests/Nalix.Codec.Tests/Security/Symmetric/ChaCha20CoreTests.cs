// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Security.Symmetric;

namespace Nalix.Codec.Tests.Security.Symmetric;

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
    public void KeyStreamOneBlock_WithSeededState_MatchesScalarKeyStream(int seed)
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
    public void KeyStreamTwoBlocks_WithSeededState_MatchesScalarKeyStreamForBothCounters(int seed)
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
    public void KeyStream_OnEveryPath_LeavesStateUnchanged()
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
    public void Xor_ForEveryLength_MatchesByteWiseXor()
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

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(7, 1u)]
    [InlineData(0x1234, 0xFFFFFFF8u)]
    public void XorEightBlocks_WithSeededState_MatchesScalarKeyStreamForEachCounter(int seed, uint counter)
    {
        if (!ChaCha20Core.EightBlockPathSupported)
        {
            return;
        }

        uint[] state = StateFrom(seed);
        byte[] src = new byte[8 * ChaCha20.BlockSize];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (byte)(i * 7);
        }

        byte[] actual = new byte[src.Length];
        ChaCha20Core.XorEightBlocks(state, counter, src, actual);

        for (int block = 0; block < 8; block++)
        {
            state[12] = counter + (uint)block;
            byte[] keystream = new byte[ChaCha20.BlockSize];
            ChaCha20Core.KeyStreamScalar(state, keystream);

            for (int i = 0; i < ChaCha20.BlockSize; i++)
            {
                int at = (block * ChaCha20.BlockSize) + i;
                Assert.Equal((byte)(src[at] ^ keystream[i]), actual[at]);
            }
        }
    }

    [Fact]
    public void Encrypt_ForLengthsAcrossEveryPath_MatchesScalarKeyStream()
    {
        // Lengths around the 8-, 2- and 1-block boundaries, so each path hands over to the next one.
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] nonce = new byte[12];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i + 1);
        }

        nonce[3] = 0x4A;

        foreach (int length in new[] { 0, 1, 63, 64, 65, 127, 128, 129, 511, 512, 513, 640, 1023, 1024, 1200, 1471, 4096 + 77 })
        {
            byte[] plaintext = new byte[length];
            for (int i = 0; i < length; i++)
            {
                plaintext[i] = (byte)(i ^ 0x5C);
            }

            byte[] actual = new byte[length];
            ChaCha20 cipher = new(key, nonce, 1);
            _ = cipher.Encrypt(plaintext, actual);

            Assert.Equal(ScalarEncrypt(key, nonce, 1, plaintext), actual);
        }
    }

    [Fact]
    public void Encrypt_WhenTheCounterWouldWrapInsideEightBlocks_ThrowsBeforeWritingOutput()
    {
        // Counters 0xFFFFFFFC.. would wrap to 0 inside the first eight-block pass: no keystream may be reused.
        byte[] plaintext = new byte[8 * ChaCha20.BlockSize];
        byte[] output = new byte[plaintext.Length];
        ChaCha20 cipher = new(new byte[ChaCha20.KeySize], new byte[12], 0xFFFFFFFCu);

        _ = Assert.ThrowsAny<CipherException>(() => cipher.Encrypt(plaintext, output));
        Assert.All(output, b => Assert.Equal(0, b));
    }

    private static byte[] ScalarEncrypt(byte[] key, byte[] nonce, uint counter, byte[] plaintext)
    {
        uint[] state = new uint[ChaCha20.StateLength];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (int i = 0; i < 8; i++)
        {
            state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        }

        state[12] = counter;
        for (int i = 0; i < 3; i++)
        {
            state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(i * 4));
        }

        byte[] output = new byte[plaintext.Length];
        byte[] keystream = new byte[ChaCha20.BlockSize];
        for (int offset = 0; offset < plaintext.Length; offset += ChaCha20.BlockSize)
        {
            ChaCha20Core.KeyStreamScalar(state, keystream);
            state[12]++;
            for (int i = 0; i < ChaCha20.BlockSize && offset + i < plaintext.Length; i++)
            {
                output[offset + i] = (byte)(plaintext[offset + i] ^ keystream[i]);
            }
        }

        return output;
    }
}
