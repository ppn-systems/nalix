// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Numerics;
using Nalix.Codec.Security.Hashing;
using Xunit;

namespace Nalix.Framework.Tests.Cryptography;

/// <summary>
/// Checks the limb-based Poly1305 against a deliberately naive big-integer transcription of
/// RFC 8439 §2.5.1, over a wide sweep of lengths and keys.
/// </summary>
/// <remarks>
/// The reference below is written to be obviously the specification rather than to be fast: it
/// keeps the accumulator as a <see cref="BigInteger"/> and reduces with <c>%</c> after every block.
/// </remarks>
public sealed class Poly1305ReferenceTests
{
    private static readonly BigInteger Prime = BigInteger.Pow(2, 130) - 5;

    private static BigInteger LittleEndian(ReadOnlySpan<byte> bytes)
        => new(bytes, isUnsigned: true, isBigEndian: false);

    private static byte[] ReferenceTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        Span<byte> clamped = stackalloc byte[16];
        key[..16].CopyTo(clamped);

        // RFC 8439 §2.5: clear the top four bits of every fourth byte and the low two bits of
        // bytes 4, 8 and 12.
        clamped[3] &= 15;
        clamped[7] &= 15;
        clamped[11] &= 15;
        clamped[15] &= 15;
        clamped[4] &= 252;
        clamped[8] &= 252;
        clamped[12] &= 252;

        BigInteger r = LittleEndian(clamped);
        BigInteger s = LittleEndian(key.Slice(16, 16));
        BigInteger accumulator = BigInteger.Zero;

        for (int offset = 0; offset < message.Length; offset += 16)
        {
            int take = Math.Min(16, message.Length - offset);

            Span<byte> block = stackalloc byte[17];
            block.Clear();
            message.Slice(offset, take).CopyTo(block);
            block[take] = 0x01;

            accumulator = (accumulator + LittleEndian(block[..(take + 1)])) * r % Prime;
        }

        accumulator += s;

        byte[] tag = new byte[16];
        BigInteger truncated = accumulator & ((BigInteger.One << 128) - 1);

        byte[] raw = truncated.ToByteArray(isUnsigned: true, isBigEndian: false);
        raw.AsSpan(0, Math.Min(raw.Length, 16)).CopyTo(tag);

        return tag;
    }

    private static byte[] KeyFrom(int seed)
    {
        byte[] key = new byte[Poly1305.KeySize];
        uint state = (uint)seed * 0x9E3779B1u + 0x85EBCA6Bu;

        for (int i = 0; i < key.Length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            key[i] = (byte)(state >> 24);
        }

        return key;
    }

    [Fact]
    public void ComputeMatchesTheSpecificationForEveryLengthAcrossFourBlocks()
    {
        byte[] key = KeyFrom(1);

        // 0 to 64 bytes covers an empty message, every partial-block tail, and the block-count
        // boundaries at 16, 32, 48 and 64.
        for (int length = 0; length <= 64; length++)
        {
            byte[] message = new byte[length];
            for (int i = 0; i < length; i++)
            {
                message[i] = (byte)(i * 11);
            }

            byte[] expected = ReferenceTag(key, message);
            byte[] actual = Poly1305.Compute(key, message);

            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(0x5EED)]
    public void ComputeMatchesTheSpecificationForVariedKeysAndLongMessages(int seed)
    {
        byte[] key = KeyFrom(seed);

        foreach (int length in new[] { 1, 15, 16, 17, 255, 256, 1023, 1024, 4097 })
        {
            byte[] message = new byte[length];
            for (int i = 0; i < length; i++)
            {
                message[i] = (byte)((i * seed) ^ (i >> 3));
            }

            Assert.Equal(ReferenceTag(key, message), Poly1305.Compute(key, message));
        }
    }

    /// <summary>
    /// The accumulator is left partially reduced between blocks, so a value that sits just under,
    /// on, or just over the prime only shows up with all-ones input. This pins the final reduction.
    /// </summary>
    [Fact]
    public void ComputeMatchesTheSpecificationForSaturatedInput()
    {
        byte[] key = new byte[Poly1305.KeySize];
        Array.Fill(key, (byte)0xFF);

        foreach (int length in new[] { 16, 32, 48, 64, 128 })
        {
            byte[] message = new byte[length];
            Array.Fill(message, (byte)0xFF);

            Assert.Equal(ReferenceTag(key, message), Poly1305.Compute(key, message));
        }
    }

    [Fact]
    public void IncrementalUpdatesMatchTheOneShotTagWhateverTheChunking()
    {
        byte[] key = KeyFrom(9);
        byte[] message = new byte[1000];
        for (int i = 0; i < message.Length; i++)
        {
            message[i] = (byte)(i * 7);
        }

        byte[] expected = Poly1305.Compute(key, message);

        foreach (int chunk in new[] { 1, 3, 7, 15, 16, 17, 64, 333 })
        {
            byte[] actual = new byte[Poly1305.TagSize];
            Poly1305 mac = new(key);

            try
            {
                for (int offset = 0; offset < message.Length; offset += chunk)
                {
                    mac.Update(message.AsSpan(offset, Math.Min(chunk, message.Length - offset)));
                }

                mac.FinalizeTag(actual);
            }
            finally
            {
                mac.Clear();
            }

            Assert.Equal(expected, actual);
        }
    }
}
