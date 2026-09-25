// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.LZ4;

namespace Nalix.Codec.Tests.LZ4;

/// <summary>
/// The per-thread LZ4 hash table is reused without clearing; stale entries from earlier blocks must
/// only act as (verified) hints and never corrupt the output.
/// </summary>
public sealed class LZ4HashTableReuseTests
{
    [Fact]
    public void Encode_AfterUnrelatedBlocks_StillRoundTrips()
    {
        Random rng = new(12345);

        for (int iteration = 0; iteration < 500; iteration++)
        {
            int length = rng.Next(16, 8192);
            byte[] input = new byte[length];

            // Mix of compressible runs and random noise so matches and stale hints both occur.
            int mode = iteration % 3;
            for (int i = 0; i < length; i++)
            {
                input[i] = mode switch
                {
                    0 => (byte)rng.Next(256),
                    1 => (byte)(i % 17),
                    _ => (i / 64 % 2 == 0) ? (byte)rng.Next(4) : (byte)(i * 31),
                };
            }

            byte[] compressed = new byte[length + (length / 255) + 64];
            int written = LZ4Codec.Encode(input, compressed);

            byte[] decoded = new byte[length];
            Assert.True(LZ4Codec.TryDecode(compressed.AsSpan(0, written), decoded, out int decodedLength));
            Assert.Equal(length, decodedLength);
            Assert.True(input.AsSpan().SequenceEqual(decoded));
        }
    }

    [Fact]
    public void Encode_SameInput_AfterDifferentPriorBlock_DecodesIdentically()
    {
        byte[] input = new byte[4096];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i % 29);
        }

        byte[] noise = new byte[4096];
        new Random(7).NextBytes(noise);

        byte[] scratch = new byte[8192];
        _ = LZ4Codec.Encode(noise, scratch);

        byte[] compressed = new byte[8192];
        int written = LZ4Codec.Encode(input, compressed);

        byte[] decoded = new byte[input.Length];
        Assert.True(LZ4Codec.TryDecode(compressed.AsSpan(0, written), decoded, out int n));
        Assert.Equal(input.Length, n);
        Assert.True(input.AsSpan().SequenceEqual(decoded));
        Assert.True(written < input.Length / 4, "a periodic input must still compress well with a reused table");
    }
}
