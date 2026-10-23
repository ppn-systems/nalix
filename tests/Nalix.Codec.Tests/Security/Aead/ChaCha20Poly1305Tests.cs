// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Asymmetric;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Symmetric;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nalix.Codec.Tests.Security.Aead;

/// <summary>
/// Unit tests for ChaCha20Poly1305 AEAD implementation.
/// </summary>
public sealed class ChaCha20Poly1305Tests
{
    private static byte[] RandomBytes(int length)
    {
        byte[] buf = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return buf;
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

    private static byte[] GenerateRandom(int size)
    {
        byte[] data = new byte[size];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        return data;
    }

    /// <summary>
    /// RFC 8439 §2.8.2 example and test vector for AEAD_CHACHA20_POLY1305.
    /// </summary>
    [Fact]
    public void Encrypt_WithRfc8439Section282Vector_MatchesExpectedCiphertextAndTag()
    {
        byte[] key = HexToBytes(
            "808182838485868788898a8b8c8d8e8f" +
            "909192939495969798999a9b9c9d9e9f");
        byte[] nonce = HexToBytes("07000000" + "4041424344454647");
        byte[] aad = HexToBytes("50515253c0c1c2c3c4c5c6c7");
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes(
            "Ladies and Gentlemen of the class of '99: If I could offer you " +
            "only one tip for the future, sunscreen would be it.");
        byte[] expectedCiphertext = HexToBytes(
            "d31a8d34648e60db7b86afbc53ef7ec2" +
            "a4aded51296e08fea9e2b5a736ee62d6" +
            "3dbea45e8ca9671282fafb69da92728b" +
            "1a71de0a9e060b2905d6a5b67ecd3b36" +
            "92ddbd7f2d778b8c9803aee328091b58" +
            "fab324e4fad675945585808b4831d7bc" +
            "3ff4def08e4b7a9de576d26586cec64b" +
            "6116");
        byte[] expectedTag = HexToBytes("1ae10b594f09e26a7e902ecbd0600691");

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChaCha20Poly1305.TagSize];

        int written = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext, tag);

        Assert.Equal(plaintext.Length, written);
        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.5 ChaCha20-Poly1305 AEAD decryption test vector.
    /// </summary>
    [Fact]
    public void Decrypt_WithRfc8439AppendixA5Vector_MatchesExpectedPlaintext()
    {
        byte[] key = HexToBytes(
            "1c9240a5eb55d38af333888604f6b5f0" +
            "473917c1402b80099dca5cbc207075c0");
        byte[] nonce = HexToBytes("000000000102030405060708");
        byte[] aad = HexToBytes("f33388860000000000004e91");
        byte[] ciphertext = HexToBytes(
            "64a0861575861af460f062c79be643bd" +
            "5e805cfd345cf389f108670ac76c8cb2" +
            "4c6cfc18755d43eea09ee94e382d26b0" +
            "bdb7b73c321b0100d4f03b7f355894cf" +
            "332f830e710b97ce98c8a84abd0b9481" +
            "14ad176e008d33bd60f982b1ff37c855" +
            "9797a06ef4f0ef61c186324e2b350638" +
            "3606907b6a7c02b0f9f6157b53c867e4" +
            "b9166c767b804d46a59b5216cde7a4e9" +
            "9040c5a40433225ee282a1b0a06c523e" +
            "af4534d7f83fa1155b0047718cbc546a" +
            "0d072b04b3564eea1b422273f548271a" +
            "0bb2316053fa769919" +
            "55ebd63159434e" +
            "cebb4e466dae5a1073a67276270" +
            "97a10" +
            "49e617d91d361094fa68f0ff77987130" +
            "305beaba2eda04df997b714d6c6f2c29" +
            "a6ad5cb4022b02709b");
        byte[] expectedTag = HexToBytes("eead9d67890cbb22392336fea1851f38");
        byte[] expectedPlaintext = HexToBytes(
            "496e7465726e65742d44726166747320" +
            "61726520647261667420646f63756d65" +
            "6e74732076616c696420666f72206120" +
            "6d6178696d756d206f6620736978206d" +
            "6f6e74687320616e64206d6179206265" +
            "20757064617465642c207265706c6163" +
            "65642c206f72206f62736f6c65746564" +
            "206279206f7468657220646f63756d65" +
            "6e747320617420616e792074696d652e" +
            "20497420697320696e617070726f7072" +
            "6961746520746f2075736520496e7465" +
            "726e65742d4472616674732061732072" +
            "65666572656e6365206d617465726961" +
            "6c206f7220746f206369746520746865" +
            "6d206f74686572207468616e20617320" +
            "2fe2809c776f726b20696e2070726f67" +
            "726573732e2fe2809d");

        byte[] plaintext = new byte[ciphertext.Length];

        int written = ChaCha20Poly1305.Decrypt(key, nonce, ciphertext, aad, expectedTag, plaintext);

        Assert.Equal(expectedPlaintext.Length, written);
        Assert.Equal(expectedPlaintext, plaintext);
    }

    /// <summary>
    /// Verifies round-trip correctness using the Span-based API.
    /// </summary>
    [Fact]
    public void EncryptDecrypt_WithSpanApi_RoundTripsPayload()
    {
        byte[] key = RandomBytes(32);
        byte[] nonce = RandomBytes(12);
        byte[] plaintext = RandomBytes(128);
        byte[] aad = RandomBytes(20);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChaCha20Poly1305.TagSize];
        byte[] recovered = new byte[plaintext.Length];

        int ctWritten = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext, tag);
        Assert.Equal(plaintext.Length, ctWritten);

        int ptWritten = ChaCha20Poly1305.Decrypt(key, nonce, ciphertext, aad, tag, recovered);
        Assert.Equal(plaintext.Length, ptWritten);

        Assert.Equal(plaintext, recovered);
    }

    /// <summary>
    /// Tampering with the authentication tag should cause Span-based decryption to return a negative value.
    /// </summary>
    [Fact]
    public void Decrypt_WhenTagIsTampered_ReturnsNegativeResult()
    {
        byte[] key = RandomBytes(32);
        byte[] nonce = RandomBytes(12);
        byte[] plaintext = RandomBytes(32);
        byte[] aad = RandomBytes(4);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChaCha20Poly1305.TagSize];

        _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext, tag);
        tag[0] ^= 0xFF;
        byte[] recovered = new byte[plaintext.Length];

        int result = ChaCha20Poly1305.Decrypt(key, nonce, ciphertext, aad, tag, recovered);

        Assert.True(result < 0, "Span-based Decrypt should return a negative value on authentication failure.");
    }

    [Fact]
    public void Decrypt_WhenCiphertextIsTruncated_ReturnsNegativeResult()
    {
        byte[] key = RandomBytes(32);
        byte[] nonce = RandomBytes(12);
        byte[] plaintext = RandomBytes(32);
        byte[] aad = RandomBytes(4);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChaCha20Poly1305.TagSize];
        _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext, tag);

        byte[] truncated = ciphertext.AsSpan(0, ciphertext.Length - 1).ToArray();
        byte[] recovered = new byte[truncated.Length];

        int result = ChaCha20Poly1305.Decrypt(key, nonce, truncated, aad, tag, recovered);

        Assert.True(result < 0, "Decrypt should reject a truncated ciphertext.");
    }

    [Fact]
    public void Decrypt_WhenAadIsTampered_ReturnsNegativeResult()
    {
        byte[] key = RandomBytes(32);
        byte[] nonce = RandomBytes(12);
        byte[] plaintext = RandomBytes(32);
        byte[] aad = RandomBytes(8);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChaCha20Poly1305.TagSize];
        _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext, tag);

        aad[0] ^= 0xFF;
        byte[] recovered = new byte[plaintext.Length];

        int result = ChaCha20Poly1305.Decrypt(key, nonce, ciphertext, aad, tag, recovered);

        Assert.True(result < 0, "Decrypt should reject tampered AAD.");
    }

    [Fact]
    public void Decrypt_WithWrongKey_ReturnsNegativeResult()
    {
        byte[] key = RandomBytes(32);
        byte[] wrongKey = RandomBytes(32);
        byte[] nonce = RandomBytes(12);
        byte[] plaintext = RandomBytes(32);
        byte[] aad = RandomBytes(4);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChaCha20Poly1305.TagSize];
        _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext, tag);

        byte[] recovered = new byte[plaintext.Length];
        int result = ChaCha20Poly1305.Decrypt(wrongKey, nonce, ciphertext, aad, tag, recovered);

        Assert.True(result < 0, "Decrypt should reject the wrong key.");
    }

    [Fact]
    public void Decrypt_WithWrongNonce_ReturnsNegativeResult()
    {
        byte[] key = RandomBytes(32);
        byte[] nonce = RandomBytes(12);
        byte[] wrongNonce = RandomBytes(12);
        byte[] plaintext = RandomBytes(32);
        byte[] aad = RandomBytes(4);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[ChaCha20Poly1305.TagSize];
        _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext, tag);

        byte[] recovered = new byte[plaintext.Length];
        int result = ChaCha20Poly1305.Decrypt(key, wrongNonce, ciphertext, aad, tag, recovered);

        Assert.True(result < 0, "Decrypt should reject the wrong nonce.");
    }

    [Fact]
    public void EncryptDecrypt_WithEmptyPlaintextAndNonEmptyAad_RoundTrips()
    {
        byte[] key = RandomBytes(32);
        byte[] nonce = RandomBytes(12);
        byte[] plaintext = [];
        byte[] aad = RandomBytes(16);

        byte[] ciphertext = [];
        byte[] tag = new byte[ChaCha20Poly1305.TagSize];

        int written = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext, tag);
        Assert.Equal(0, written);

        byte[] recovered = [];
        int result = ChaCha20Poly1305.Decrypt(key, nonce, ciphertext, aad, tag, recovered);

        Assert.Equal(0, result);
    }

    [Fact]
    public void Encrypt_WithSameKeyAndNonce_IsDeterministic()
    {
        byte[] key = RandomBytes(32);
        byte[] nonce = RandomBytes(12);
        byte[] plaintext = RandomBytes(64);
        byte[] aad = RandomBytes(8);

        byte[] ciphertext1 = new byte[plaintext.Length];
        byte[] tag1 = new byte[ChaCha20Poly1305.TagSize];
        _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext1, tag1);

        byte[] ciphertext2 = new byte[plaintext.Length];
        byte[] tag2 = new byte[ChaCha20Poly1305.TagSize];
        _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, ciphertext2, tag2);

        Assert.Equal(ciphertext1, ciphertext2);
        Assert.Equal(tag1, tag2);
    }

    /// <summary>
    /// Cross-validates against the BCL's <see cref="System.Security.Cryptography.ChaCha20Poly1305"/>
    /// (available when the platform provides libsodium/OpenSSL support) using seeded-RNG round trips:
    /// Nalix encrypts, BCL decrypts, and vice versa.
    /// </summary>
    [Fact]
    public void Encrypt_WithSeededRandomInputs_InteroperatesWithBclChaCha20Poly1305()
    {
        if (!System.Security.Cryptography.ChaCha20Poly1305.IsSupported)
        {
            return;
        }

        System.Random rng = new(12345);

        for (int iter = 0; iter < 100; iter++)
        {
            byte[] key = new byte[32];
            byte[] nonce = new byte[12];
            byte[] plaintext = new byte[rng.Next(0, 256)];
            byte[] aad = new byte[rng.Next(0, 32)];

            rng.NextBytes(key);
            rng.NextBytes(nonce);
            rng.NextBytes(plaintext);
            rng.NextBytes(aad);

            // Nalix encrypts, BCL decrypts.
            byte[] nalixCiphertext = new byte[plaintext.Length];
            byte[] nalixTag = new byte[ChaCha20Poly1305.TagSize];
            _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, nalixCiphertext, nalixTag);

            byte[] bclRecovered = new byte[plaintext.Length];
            using (System.Security.Cryptography.ChaCha20Poly1305 bcl = new(key))
            {
                bcl.Decrypt(nonce, nalixCiphertext, nalixTag, bclRecovered, aad);
            }

            Assert.Equal(plaintext, bclRecovered);

            // BCL encrypts, Nalix decrypts.
            byte[] bclCiphertext = new byte[plaintext.Length];
            byte[] bclTag = new byte[ChaCha20Poly1305.TagSize];
            using (System.Security.Cryptography.ChaCha20Poly1305 bcl = new(key))
            {
                bcl.Encrypt(nonce, plaintext, bclCiphertext, bclTag, aad);
            }

            byte[] nalixRecovered = new byte[plaintext.Length];
            int written = ChaCha20Poly1305.Decrypt(key, nonce, bclCiphertext, aad, bclTag, nalixRecovered);

            Assert.Equal(plaintext.Length, written);
            Assert.Equal(plaintext, nalixRecovered);
        }
    }

    [Fact]
    public void Encrypt_WithRandomInputs_MatchesBouncyCastle()
    {
        byte[] key = GenerateRandom(32);
        byte[] nonce = GenerateRandom(12);
        byte[] aad = GenerateRandom(64);
        byte[] plaintext = GenerateRandom(1024);
        byte[] nalixCipher = new byte[1024];
        byte[] nalixTag = new byte[16];

        // Nalix
        Nalix.Codec.Security.Aead.ChaCha20Poly1305.Encrypt(key, nonce, plaintext, aad, nalixCipher, nalixTag);

        // BouncyCastle
        var engine = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        engine.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, aad));
        byte[] bcOutput = new byte[plaintext.Length + 16];
        int len = engine.ProcessBytes(plaintext, 0, plaintext.Length, bcOutput, 0);
        engine.DoFinal(bcOutput, len);

        byte[] bcCipher = bcOutput[..plaintext.Length];
        byte[] bcTag = bcOutput[plaintext.Length..];

        Assert.Equal(bcCipher, nalixCipher);
        Assert.Equal(bcTag, nalixTag);
    }
}
