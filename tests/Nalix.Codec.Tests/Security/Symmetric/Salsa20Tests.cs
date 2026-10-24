// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Security.Cryptography;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Asymmetric;
using Nalix.Codec.Security.Engine;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Symmetric;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nalix.Codec.Tests.Security.Symmetric;

public sealed class Salsa20Tests
{
    private static byte[] SequentialBytes(int length, int start = 0)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(start + i);
        }

        return data;
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] data = new byte[hex.Length / 2];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return data;
    }

    private static readonly byte[] s_key32 = new byte[32];

    private static readonly byte[] s_nonce8 = new byte[8];

    private static readonly byte[] s_plaintextShort =
        System.Text.Encoding.UTF8.GetBytes("Hello, Nalix!");

    private static byte[] GenerateRandom(int size)
    {
        byte[] data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void EncryptDecrypt_WithSupportedKeyLengths_RoundTrips(int keyLength)
    {
        byte[] key = SequentialBytes(keyLength, 1);
        byte[] nonce = SequentialBytes(Salsa20.NonceSize, 50);
        byte[] plaintext = SequentialBytes(170, 100);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] recovered = new byte[plaintext.Length];

        int encrypted = Salsa20.Encrypt(key, nonce, 3UL, plaintext, ciphertext);
        int decrypted = Salsa20.Decrypt(key, nonce, 3UL, ciphertext, recovered);

        Assert.Equal(plaintext.Length, encrypted);
        Assert.Equal(ciphertext.Length, decrypted);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void Encrypt_WithSameInputs_IsDeterministic()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] nonce = SequentialBytes(Salsa20.NonceSize, 3);
        byte[] plaintext = SequentialBytes(128, 11);

        byte[] c1 = new byte[plaintext.Length];
        byte[] c2 = new byte[plaintext.Length];

        _ = Salsa20.Encrypt(key, nonce, 99UL, plaintext, c1);
        _ = Salsa20.Encrypt(key, nonce, 99UL, plaintext, c2);

        Assert.Equal(c1, c2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(31)]
    public void Encrypt_WithInvalidKeyLength_ThrowsArgumentException(int keyLength)
    {
        byte[] key = new byte[keyLength];
        byte[] nonce = new byte[Salsa20.NonceSize];
        byte[] plaintext = new byte[8];
        byte[] ciphertext = new byte[8];

        _ = Assert.Throws<ArgumentException>(() => Salsa20.Encrypt(key, nonce, 0UL, plaintext, ciphertext));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void Encrypt_WithInvalidNonceLength_ThrowsArgumentException(int nonceLength)
    {
        byte[] key = new byte[32];
        byte[] nonce = new byte[nonceLength];
        byte[] plaintext = new byte[8];
        byte[] ciphertext = new byte[8];

        _ = Assert.Throws<ArgumentException>(() => Salsa20.Encrypt(key, nonce, 0UL, plaintext, ciphertext));
    }

    /// <summary>
    /// eSTREAM Salsa20/20 "full-verified" test vectors, Set 1, vector# 0, 128-bit key.
    /// Source: https://raw.githubusercontent.com/das-labor/legacy/master/microcontroller-2/crypto-lib/testvectors/salsa20-full-verified.test-vectors
    /// </summary>
    [Fact]
    public void Encrypt_WithEstreamSet1Vector0And128BitKey_MatchesExpectedStream()
    {
        byte[] key = HexToBytes("80000000000000000000000000000000");
        byte[] nonce = HexToBytes("0000000000000000");
        byte[] zeros = new byte[64];
        byte[] expectedStream = HexToBytes(
            "4DFA5E481DA23EA09A31022050859936" +
            "DA52FCEE218005164F267CB65F5CFD7F" +
            "2B4F97E0FF16924A52DF269515110A07" +
            "F9E460BC65EF95DA58F740B7D1DBB0AA");
        byte[] actual = new byte[64];

        int written = Salsa20.Encrypt(key, nonce, 0UL, zeros, actual);

        Assert.Equal(64, written);
        Assert.Equal(expectedStream, actual);
    }

    /// <summary>
    /// eSTREAM Salsa20/20 "full-verified" test vectors, Set 1, vector# 0, 256-bit key.
    /// Source: https://raw.githubusercontent.com/alexwebr/salsa20/master/test_vectors.256
    /// </summary>
    [Fact]
    public void Encrypt_WithEstreamSet1Vector0And256BitKey_MatchesExpectedStream()
    {
        byte[] key = HexToBytes(
            "80000000000000000000000000000000" +
            "00000000000000000000000000000000");
        byte[] nonce = HexToBytes("0000000000000000");
        byte[] zeros = new byte[64];
        byte[] expectedStream = HexToBytes(
            "E3BE8FDD8BECA2E3EA8EF9475B29A6E7" +
            "003951E1097A5C38D23B7A5FAD9F6844" +
            "B22C97559E2723C7CBBD3FE4FC8D9A07" +
            "44652A83E72A9C461876AF4D7EF1A117");
        byte[] actual = new byte[64];

        int written = Salsa20.Encrypt(key, nonce, 0UL, zeros, actual);

        Assert.Equal(64, written);
        Assert.Equal(expectedStream, actual);
    }

    [Fact]
    public void Decrypt_WhenDestinationIsTooSmall_ThrowsArgumentException()
    {
        byte[] key = SequentialBytes(32);
        byte[] nonce = SequentialBytes(Salsa20.NonceSize);
        byte[] ciphertext = SequentialBytes(32, 10);
        byte[] tooSmall = new byte[ciphertext.Length - 1];

        _ = Assert.Throws<ArgumentException>(() => Salsa20.Decrypt(key, nonce, 0UL, ciphertext, tooSmall));
    }

    [Fact]
    public void Encrypt_WhenOutputBufferIsTooSmall_ThrowsArgumentException()
    {
        byte[] tooSmall = new byte[1];
        _ = Assert.Throws<ArgumentException>(() =>
            Salsa20.Encrypt(s_key32, s_nonce8, 0UL, s_plaintextShort, tooSmall));
    }

    [Fact]
    public void Encrypt_WithSpanOverload_ReturnsPlaintextLength()
    {
        byte[] dst = new byte[s_plaintextShort.Length];
        int written = Salsa20.Encrypt(s_key32, s_nonce8, 0UL, s_plaintextShort, dst);
        Assert.Equal(s_plaintextShort.Length, written);
    }

    [Fact]
    public void Encrypt_WithRandomInputs_MatchesBouncyCastle()
    {
        byte[] key = GenerateRandom(32);
        byte[] nonce = GenerateRandom(8); // Standard 64-bit nonce
        byte[] plaintext = GenerateRandom(1024);
        byte[] nalixCipher = new byte[1024];

        // Nalix
        Salsa20.Encrypt(key, nonce, 0, plaintext, nalixCipher);

        // BouncyCastle
        var bc = new Salsa20Engine();
        bc.Init(true, new ParametersWithIV(new KeyParameter(key), nonce));
        byte[] bcCipher = new byte[1024];
        bc.ProcessBytes(plaintext, 0, plaintext.Length, bcCipher, 0);

        Assert.Equal(bcCipher, nalixCipher);
    }
}
