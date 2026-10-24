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
/// Unit tests for Keccak256. This codebase implements the original Keccak
/// (Ethereum-style) domain padding byte 0x01 — NOT FIPS 202 SHA3-256 (domain 0x06).
/// The two produce different digests for identical input, so cross-validation
/// against System.Security.Cryptography.SHA3_256 is not possible; vectors below
/// are the well-known published Ethereum Keccak-256 reference digests for
/// empty input and "abc" (see e.g. https://emn178.github.io/online-tools/keccak_256.html
/// and the Ethereum Yellow Paper's Keccak-256 Appendix, both reproduced widely as
/// the canonical Keccak-256("") and Keccak-256("abc") reference values).
/// </summary>
public sealed class Keccak256Tests
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
    /// Canonical Ethereum Keccak-256 reference digest for the empty input.
    /// </summary>
    [Fact]
    public void HashData_WithEmptyInput_MatchesCanonicalKeccak256Vector()
    {
        byte[] expected = HexToBytes("c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");

        byte[] actual = Keccak256.HashData([]);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Canonical Ethereum Keccak-256 reference digest for the ASCII input "abc".
    /// </summary>
    [Fact]
    public void HashData_WithAbcInput_MatchesCanonicalKeccak256Vector()
    {
        byte[] expected = HexToBytes("4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45");

        byte[] actual = Keccak256.HashData(System.Text.Encoding.ASCII.GetBytes("abc"));

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Exercises the streaming Sponge path (input larger than the 136-byte rate,
    /// requiring multiple absorbed blocks) rather than the one-shot fast path.
    /// This is a 137-byte all-zero input (one full rate block + one byte).
    /// Nalix-specific regression pin (no external standard vector exists for this
    /// exact input) — captured from this implementation's current output.
    /// </summary>
    [Fact]
    public void HashData_WithMultiBlockInput_MatchesRegressionVector()
    {
        byte[] input = new byte[137];
        byte[] expected = HexToBytes("BEE7FBB405CB0D91A8775E338C4A5E4B5D6B2D051F687FA942043CFFDC73BD28");

        byte[] actual = Keccak256.HashData(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void HashDataToFixed_WithAbcInput_MatchesHashData()
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes("abc");

        Bytes32 fixedHash = Keccak256.HashDataToFixed(input);
        byte[] arrayHash = Keccak256.HashData(input);

        Assert.Equal(arrayHash, fixedHash.AsSpan().ToArray());
    }

    [Fact]
    public void TryHashData_WhenOutputIsOneByteTooSmall_ReturnsFalse()
    {
        byte[] input = [1, 2, 3];
        byte[] tooSmall = new byte[31];

        bool result = Keccak256.TryHashData(input, tooSmall);

        Assert.False(result);
    }

    [Fact]
    public void HashData_WithSameInput_IsDeterministic()
    {
        byte[] input = System.Text.Encoding.ASCII.GetBytes("Nalix Keccak256 determinism check");

        byte[] hash1 = Keccak256.HashData(input);
        byte[] hash2 = Keccak256.HashData(input);

        Assert.Equal(hash1, hash2);
        Assert.NotEqual(new byte[32], hash1);
    }

    [Fact]
    public void HashData_WithEmptySpan_ReturnsKnownDigest()
    {
        byte[] hash = Keccak256.HashData(ReadOnlySpan<byte>.Empty);

        Assert.Equal(
            "C5D2460186F7233C927E7DB2DCC703C0E500B653CA82273B7BFAD8045D85A470",
            Convert.ToHexString(hash));
    }

    [Fact]
    public void HashData_WithAsciiAbc_ReturnsKnownDigest()
    {
        byte[] hash = Keccak256.HashData(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal(
            "4E03657AEA45A94FC7D47BA826C8D667C0D1E6E33A64A036EC44F58FA12D6C45",
            Convert.ToHexString(hash));
    }

    [Fact]
    public void TryHashData_WhenOutputIsSixteenBytes_ReturnsFalse()
    {
        Span<byte> output = stackalloc byte[16];

        bool ok = Keccak256.TryHashData([1, 2, 3], output);

        Assert.False(ok);
    }

    [Fact]
    public void HashData_WithRandomInput_MatchesBouncyCastle()
    {
        byte[] data = GenerateRandom(1024);

        // Nalix
        byte[] nalixHash = Keccak256.HashData(data);

        // BouncyCastle
        KeccakDigest bcDigest = new KeccakDigest(256);
        bcDigest.BlockUpdate(data, 0, data.Length);
        byte[] bcHash = new byte[32];
        bcDigest.DoFinal(bcHash, 0);

        Assert.Equal(bcHash, nalixHash);
    }
}
