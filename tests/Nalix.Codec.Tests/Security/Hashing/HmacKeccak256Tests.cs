// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Asymmetric;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Primitives;
using Nalix.Codec.Security.Symmetric;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nalix.Codec.Tests.Security.Hashing;

/// <summary>
/// Unit tests for HmacKeccak256. This is a Nalix-specific HMAC construction over the
/// Ethereum-style Keccak-256 primitive (RFC 2104 HMAC structure, non-standard hash).
/// No external published test vectors exist for HMAC-over-Ethereum-Keccak256, so these
/// are regression known-answer tests pinning the current implementation's exact output.
/// </summary>
public sealed class HmacKeccak256Tests
{
    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return result;
    }

    private static byte[] GenerateRandom(int size)
    {
        byte[] data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    /// <summary>
    /// Regression KAT: short key (&lt; 136-byte block size), short data.
    /// </summary>
    [Fact]
    public void Compute_WithShortKey_MatchesRegressionVector()
    {
        byte[] key = System.Text.Encoding.ASCII.GetBytes("nalix-hmac-key");
        byte[] data = System.Text.Encoding.ASCII.GetBytes("Nalix HmacKeccak256 regression payload");
        byte[] expected = HexToBytes("AB6D821261C9E150445DE17FED36ECACE604D2F35F008E1982DC7D9CD612972D");

        byte[] output = new byte[32];
        HmacKeccak256.Compute(key, data, output);

        Assert.Equal(expected, output);
    }

    /// <summary>
    /// Regression KAT: key longer than the 136-byte block size, forcing the
    /// key-hashing branch (<c>Keccak256.HashData(key)</c>) inside <see cref="HmacKeccak256.Compute"/>.
    /// </summary>
    [Fact]
    public void Compute_WithKeyLongerThanBlockSize_MatchesRegressionVector()
    {
        byte[] key = new byte[200];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)i;
        }

        byte[] data = System.Text.Encoding.ASCII.GetBytes("Nalix HmacKeccak256 long-key regression payload");
        byte[] expected = HexToBytes("113D687ECF3E96C89D30ED99D801F0C557FA7860D8BECDDC971421C5624F2FB7");

        byte[] output = new byte[32];
        HmacKeccak256.Compute(key, data, output);

        Assert.Equal(expected, output);
    }

    [Fact]
    public void Compute_WithSameInput_IsDeterministic()
    {
        byte[] key = System.Text.Encoding.ASCII.GetBytes("determinism-key");
        byte[] data = System.Text.Encoding.ASCII.GetBytes("determinism-data");

        byte[] out1 = new byte[32];
        byte[] out2 = new byte[32];
        HmacKeccak256.Compute(key, data, out1);
        HmacKeccak256.Compute(key, data, out2);

        Assert.Equal(out1, out2);
        Assert.NotEqual(new byte[32], out1);
    }

    [Fact]
    public void Compute_WithDifferentKeys_ProducesDifferentOutput()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("same-data");
        byte[] key1 = System.Text.Encoding.ASCII.GetBytes("key-one");
        byte[] key2 = System.Text.Encoding.ASCII.GetBytes("key-two");

        byte[] out1 = new byte[32];
        byte[] out2 = new byte[32];
        HmacKeccak256.Compute(key1, data, out1);
        HmacKeccak256.Compute(key2, data, out2);

        Assert.NotEqual(out1, out2);
    }

    [Fact]
    public void Compute_WhenOutputIsTooSmall_ThrowsArgumentException()
    {
        byte[] key = [1, 2, 3];
        byte[] data = [4, 5, 6];
        byte[] tooSmall = new byte[31];

        _ = Assert.Throws<ArgumentException>(() => HmacKeccak256.Compute(key, data, tooSmall));
    }

    [Fact]
    public void Compute_WithRepeatedInputAndChangedKey_IsDeterministicAndKeySensitive()
    {
        byte[] key1 = Encoding.ASCII.GetBytes("key-one");
        byte[] key2 = Encoding.ASCII.GetBytes("key-two");
        byte[] data = Encoding.ASCII.GetBytes("payload");
        Span<byte> mac1 = stackalloc byte[32];
        Span<byte> mac2 = stackalloc byte[32];
        Span<byte> mac3 = stackalloc byte[32];

        HmacKeccak256.Compute(key1, data, mac1);
        HmacKeccak256.Compute(key1, data, mac2);
        HmacKeccak256.Compute(key2, data, mac3);

        Assert.True(mac1.SequenceEqual(mac2));
        Assert.False(mac1.SequenceEqual(mac3));
    }

    [Fact]
    public void Compute_WithThirtyOneByteOutput_ThrowsArgumentException()
    {
        Span<byte> output = stackalloc byte[31];
        ArgumentException? exception = null;
        try
        {
            HmacKeccak256.Compute([1], [2], output);
        }
        catch (ArgumentException ex)
        {
            exception = ex;
        }

        Assert.NotNull(exception);
    }

    [Fact]
    public void Compute_WithRandomInput_MatchesBouncyCastle()
    {
        byte[] key = GenerateRandom(64);
        byte[] data = GenerateRandom(1024);
        byte[] nalixTag = new byte[32];

        // Nalix
        HmacKeccak256.Compute(key, data, nalixTag);

        // BouncyCastle
        var digest = new KeccakDigest(256);
        var hmac = new HMac(digest);
        hmac.Init(new KeyParameter(key));
        hmac.BlockUpdate(data, 0, data.Length);
        byte[] bcTag = new byte[32];
        hmac.DoFinal(bcTag, 0);

        Assert.Equal(bcTag, nalixTag);
    }
}
