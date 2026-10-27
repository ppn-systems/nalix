// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Codec.Security.Hashing;
using Org.BouncyCastle.Crypto.Digests;
using Xunit;

namespace Nalix.Codec.Tests.Security.Hashing;

/// <summary>
/// Boundary tests around the 136-byte Keccak-256 rate where the one-shot fast path hands over to the sponge.
/// </summary>
public sealed class Keccak256BoundaryTests
{
    [Theory]
    [InlineData(135)]
    [InlineData(136)]
    [InlineData(137)]
    public void HashData_RateBoundaryLengths_MatchesStreamingSpongeAndReference(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 31 + 7);
        }

        byte[] oneShot = new byte[32];
        Keccak256.HashData(data, oneShot);

        Keccak256.Sponge sponge = new();
        sponge.Absorb(data);
        byte[] streaming = new byte[32];
        sponge.PadAndSqueeze(streaming);

        KeccakDigest reference = new(256);
        reference.BlockUpdate(data, 0, data.Length);
        byte[] expected = new byte[32];
        reference.DoFinal(expected, 0);

        Assert.Equal(expected, streaming);
        Assert.Equal(expected, oneShot);
    }
}
