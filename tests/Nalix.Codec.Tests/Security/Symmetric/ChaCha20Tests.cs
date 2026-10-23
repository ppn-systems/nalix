// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Security.Cryptography;
using System.Text;
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

public sealed class ChaCha20Tests
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

    private static byte[] GenerateRandom(int size)
    {
        byte[] data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    [Fact]
    public void EncryptDecrypt_AcrossMultipleBlocks_RoundTrips()
    {
        byte[] key = SequentialBytes(ChaCha20.KeySize, 1);
        byte[] nonce = SequentialBytes(ChaCha20.NonceSize, 50);
        byte[] plaintext = SequentialBytes(ChaCha20.BlockSize * 2 + 17, 100);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] decrypted = new byte[plaintext.Length];

        ChaCha20 encryptor = new(key, nonce, 7u);
        int written = encryptor.Encrypt(plaintext, ciphertext);
        encryptor.Clear();

        ChaCha20 decryptor = new(key, nonce, 7u);
        int recovered = decryptor.Decrypt(ciphertext, decrypted);
        decryptor.Clear();

        Assert.Equal(plaintext.Length, written);
        Assert.Equal(ciphertext.Length, recovered);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Ctor_WithInvalidKeyLength_ThrowsCipherException()
    {
        byte[] invalidKey = new byte[ChaCha20.KeySize - 1];
        byte[] nonce = new byte[ChaCha20.NonceSize];

        _ = Assert.ThrowsAny<CipherException>(() => new ChaCha20(invalidKey, nonce, 0u));
    }

    [Fact]
    public void Ctor_WithInvalidNonceLength_ThrowsCipherException()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] invalidNonce = new byte[ChaCha20.NonceSize - 1];

        _ = Assert.ThrowsAny<CipherException>(() => new ChaCha20(key, invalidNonce, 0u));
    }

    [Fact]
    public void Encrypt_WhenDestinationIsTooSmall_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(ChaCha20.KeySize);
        byte[] nonce = SequentialBytes(ChaCha20.NonceSize);
        byte[] plaintext = SequentialBytes(10);
        byte[] destination = new byte[plaintext.Length - 1];

        ChaCha20 cipher = new(key, nonce, 0u);

        _ = Assert.ThrowsAny<CipherException>(() => cipher.Encrypt(plaintext, destination));

        cipher.Clear();
    }

    [Fact]
    public void Operations_AfterClear_ThrowObjectDisposedException()
    {
        byte[] key = SequentialBytes(ChaCha20.KeySize);
        byte[] nonce = SequentialBytes(ChaCha20.NonceSize);
        byte[] input = SequentialBytes(8);
        byte[] output = new byte[input.Length];
        byte[] block = new byte[ChaCha20.BlockSize];

        ChaCha20 cipher = new(key, nonce, 0u);
        cipher.Clear();

        _ = Assert.Throws<ObjectDisposedException>(() => cipher.GenerateKeyBlock(block));
        _ = Assert.Throws<ObjectDisposedException>(() => cipher.Encrypt(input, output));
        _ = Assert.Throws<ObjectDisposedException>(() => cipher.Decrypt(input, output));
    }

    [Fact]
    public void GenerateKeyBlock_WhenCounterIsMaxValue_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(ChaCha20.KeySize);
        byte[] nonce = SequentialBytes(ChaCha20.NonceSize);
        byte[] block = new byte[ChaCha20.BlockSize];

        ChaCha20 cipher = new(key, nonce, uint.MaxValue);

        _ = Assert.ThrowsAny<CipherException>(() => cipher.GenerateKeyBlock(block));
    }

    /// <summary>
    /// RFC 8439 Appendix A.1, Test Vector #1: all-zero key/nonce, block counter 0.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439AppendixA1Vector1_MatchesExpectedBlock()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] nonce = new byte[ChaCha20.NonceSize];
        byte[] expected = HexToBytes(
            "76b8e0ada0f13d90405d6ae55386bd28" +
            "bdd219b8a08ded1aa836efcc8b770dc7" +
            "da41597c5157488d7724e03fb8d84a37" +
            "6a43b8f41518a11cc387b669b2ee6586");
        byte[] actual = new byte[ChaCha20.BlockSize];

        ChaCha20 cipher = new(key, nonce, 0u);
        cipher.GenerateKeyBlock(actual);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// RFC 8439 Appendix A.1, Test Vector #2: all-zero key/nonce, block counter 1.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439AppendixA1Vector2_MatchesExpectedBlock()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] nonce = new byte[ChaCha20.NonceSize];
        byte[] expected = HexToBytes(
            "9f07e7be5551387a98ba977c732d080d" +
            "cb0f29a048e3656912c6533e32ee7aed" +
            "29b721769ce64e43d57133b074d839d5" +
            "31ed1f28510afb45ace10a1f4b794d6f");
        byte[] actual = new byte[ChaCha20.BlockSize];

        ChaCha20 cipher = new(key, nonce, 1u);
        cipher.GenerateKeyBlock(actual);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// RFC 8439 Appendix A.1, Test Vector #3: key ends in 0x01, block counter 1.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439AppendixA1Vector3_MatchesExpectedBlock()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        key[31] = 1;
        byte[] nonce = new byte[ChaCha20.NonceSize];
        byte[] expected = HexToBytes(
            "3aeb5224ecf849929b9d828db1ced4dd" +
            "832025e8018b8160b82284f3c949aa5a" +
            "8eca00bbb4a73bdad192b5c42f73f2fd" +
            "4e273644c8b36125a64addeb006c13a0");
        byte[] actual = new byte[ChaCha20.BlockSize];

        ChaCha20 cipher = new(key, nonce, 1u);
        cipher.GenerateKeyBlock(actual);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// RFC 8439 Appendix A.1, Test Vector #4: key[1]=0xff, block counter 2.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439AppendixA1Vector4_MatchesExpectedBlock()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        key[1] = 0xff;
        byte[] nonce = new byte[ChaCha20.NonceSize];
        byte[] expected = HexToBytes(
            "72d54dfbf12ec44b362692df94137f32" +
            "8fea8da73990265ec1bbbea1ae9af0ca" +
            "13b25aa26cb4a648cb9b9d1be65b2c09" +
            "24a66c54d545ec1b7374f4872e99f096");
        byte[] actual = new byte[ChaCha20.BlockSize];

        ChaCha20 cipher = new(key, nonce, 2u);
        cipher.GenerateKeyBlock(actual);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// RFC 8439 Appendix A.1, Test Vector #5: all-zero key, nonce ends in 0x02, block counter 0.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439AppendixA1Vector5_MatchesExpectedBlock()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] nonce = new byte[ChaCha20.NonceSize];
        nonce[11] = 2;
        byte[] expected = HexToBytes(
            "c2c64d378cd536374ae204b9ef933fcd" +
            "1a8b2288b3dfa49672ab765b54ee27c7" +
            "8a970e0e955c14f3a88e741b97c286f7" +
            "5f8fc299e8148362fa198a39531bed6d");
        byte[] actual = new byte[ChaCha20.BlockSize];

        ChaCha20 cipher = new(key, nonce, 0u);
        cipher.GenerateKeyBlock(actual);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// RFC 8439 Appendix A.2, Test Vector #1: all-zero key/nonce/counter, 64-byte all-zero plaintext.
    /// </summary>
    [Fact]
    public void Encrypt_WithRfc8439AppendixA2Vector1_MatchesExpectedCiphertext()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] nonce = new byte[ChaCha20.NonceSize];
        byte[] plaintext = new byte[64];
        byte[] expected = HexToBytes(
            "76b8e0ada0f13d90405d6ae55386bd28" +
            "bdd219b8a08ded1aa836efcc8b770dc7" +
            "da41597c5157488d7724e03fb8d84a37" +
            "6a43b8f41518a11cc387b669b2ee6586");
        byte[] actual = new byte[64];

        ChaCha20 cipher = new(key, nonce, 0u);
        int written = cipher.Encrypt(plaintext, actual);

        Assert.Equal(64, written);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// RFC 8439 Appendix A.2, Test Vector #3: key = 1c9240...075c0, nonce ends 0x02,
    /// counter 42, "'Twas brillig..." plaintext (127 bytes).
    /// </summary>
    [Fact]
    public void Encrypt_WithRfc8439AppendixA2Vector3_MatchesExpectedCiphertext()
    {
        byte[] key = HexToBytes(
            "1c9240a5eb55d38af333888604f6b5f0" +
            "473917c1402b80099dca5cbc207075c0");
        byte[] nonce = new byte[ChaCha20.NonceSize];
        nonce[11] = 2;
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes(
            "'Twas brillig, and the slithy toves\n" +
            "Did gyre and gimble in the wabe:\n" +
            "All mimsy were the borogoves,\n" +
            "And the mome raths outgrabe.");
        byte[] expected = HexToBytes(
            "62e6347f95ed87a45ffae7426f27a1df" +
            "5fb69110044c0d73118effa95b01e5cf" +
            "166d3df2d721caf9b21e5fb14c616871" +
            "fd84c54f9d65b283196c7fe4f60553eb" +
            "f39c6402c42234e32a356b3e764312a6" +
            "1a5532055716ead6962568f87d3f3f77" +
            "04c6a8d1bcd1bf4d50d6154b6da731b1" +
            "87b58dfd728afa36757a797ac188d1");
        byte[] actual = new byte[plaintext.Length];

        ChaCha20 cipher = new(key, nonce, 42u);
        int written = cipher.Encrypt(plaintext, actual);

        Assert.Equal(plaintext.Length, written);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Covers the block-pair path, the single-block path and every tail length in one sweep: a
    /// stream encrypted in one call must equal the same stream encrypted 64 bytes at a time.
    /// </summary>
    [Fact]
    public void Encrypt_WhenSplitIntoBlocks_MatchesSingleCall()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] nonce = new byte[ChaCha20.NonceSize];

        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(0x40 + i);
        }

        for (int i = 0; i < nonce.Length; i++)
        {
            nonce[i] = (byte)(0x90 + i);
        }

        for (int length = 0; length <= 321; length++)
        {
            byte[] plaintext = new byte[length];
            for (int i = 0; i < length; i++)
            {
                plaintext[i] = (byte)(i * 7);
            }

            byte[] oneCall = new byte[length];
            ChaCha20 bulk = new(key, nonce, 1);
            try
            {
                _ = bulk.Encrypt(plaintext, oneCall);
            }
            finally
            {
                bulk.Clear();
            }

            byte[] chunked = new byte[length];
            ChaCha20 stepwise = new(key, nonce, 1);
            try
            {
                for (int offset = 0; offset < length; offset += ChaCha20.BlockSize)
                {
                    int take = Math.Min(ChaCha20.BlockSize, length - offset);
                    _ = stepwise.Encrypt(
                        plaintext.AsSpan(offset, take),
                        chunked.AsSpan(offset, take));
                }
            }
            finally
            {
                stepwise.Clear();
            }

            Assert.Equal(chunked, oneCall);
        }
    }

    /// <summary>
    /// RFC 8439 §2.6.2 example: derive the Poly1305 one-time key via
    /// ChaCha20 block counter 0.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439Section262Vector_MatchesExpectedOneTimeKey()
    {
        byte[] key = HexToBytes(
            "808182838485868788898a8b8c8d8e8f" +
            "909192939495969798999a9b9c9d9e9f");
        byte[] nonce = HexToBytes("000000000001020304050607");
        byte[] expectedOneTimeKey = HexToBytes(
            "8ad5a08b905f81cc815040274ab29471" +
            "a833b637e3fd0da508dbb8e2fdd1a646");

        ChaCha20 cipher = new(key, nonce, 0u);
        byte[] block = new byte[ChaCha20.BlockSize];
        cipher.GenerateKeyBlock(block);

        Assert.Equal(expectedOneTimeKey, block.AsSpan(0, 32).ToArray());
    }

    /// <summary>
    /// RFC 8439 Appendix A.4, Test Vector #1: all-zero key/nonce.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439AppendixA4Vector1_MatchesExpectedOneTimeKey()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] nonce = new byte[ChaCha20.NonceSize];
        byte[] expectedOneTimeKey = HexToBytes(
            "76b8e0ada0f13d90405d6ae55386bd28" +
            "bdd219b8a08ded1aa836efcc8b770dc7");

        ChaCha20 cipher = new(key, nonce, 0u);
        byte[] block = new byte[ChaCha20.BlockSize];
        cipher.GenerateKeyBlock(block);

        Assert.Equal(expectedOneTimeKey, block.AsSpan(0, 32).ToArray());
    }

    /// <summary>
    /// RFC 8439 Appendix A.4, Test Vector #2: key ends in 0x01, nonce ends in 0x02.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439AppendixA4Vector2_MatchesExpectedOneTimeKey()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        key[31] = 1;
        byte[] nonce = new byte[ChaCha20.NonceSize];
        nonce[11] = 2;
        byte[] expectedOneTimeKey = HexToBytes(
            "ecfa254f845f647473d3cb140da9e876" +
            "06cb33066c447b87bc2666dde3fbb739");

        ChaCha20 cipher = new(key, nonce, 0u);
        byte[] block = new byte[ChaCha20.BlockSize];
        cipher.GenerateKeyBlock(block);

        Assert.Equal(expectedOneTimeKey, block.AsSpan(0, 32).ToArray());
    }

    /// <summary>
    /// RFC 8439 Appendix A.4, Test Vector #3.
    /// </summary>
    [Fact]
    public void GenerateKeyBlock_WithRfc8439AppendixA4Vector3_MatchesExpectedOneTimeKey()
    {
        byte[] key = HexToBytes(
            "1c9240a5eb55d38af333888604f6b5f0" +
            "473917c1402b80099dca5cbc207075c0");
        byte[] nonce = HexToBytes("000000000000000000000002");
        byte[] expectedOneTimeKey = HexToBytes(
            "965e3bc6f9ec7ed9560808f4d229f94b" +
            "137ff275ca9b3fcbdd59deaad23310ae");

        ChaCha20 cipher = new(key, nonce, 0u);
        byte[] block = new byte[ChaCha20.BlockSize];
        cipher.GenerateKeyBlock(block);

        Assert.Equal(expectedOneTimeKey, block.AsSpan(0, 32).ToArray());
    }

    [Fact]
    public void GenerateKeyBlock_WithRfc7539Vector_MatchesExpectedKeystream()
    {
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            key[i] = (byte)i;
        }

        byte[] nonce = HexToBytes("000000090000004a00000000");

        byte[] expected = HexToBytes(
            "10f1e7e4d13b5915500fdd1fa32071c4" +
            "c7d1f4c733c068030422aa9ac3d46c4e" +
            "d2826446079faa0914c2d705d98b02a2" +
            "b5129cd1de164eb9cbd083e8a2503c4e");

        ChaCha20 cipher = new(key, nonce, 1u);
        byte[] keystream = new byte[64];
        cipher.GenerateKeyBlock(keystream);
        cipher.Clear();

        Assert.Equal(expected, keystream);
    }

    [Fact]
    public void Encrypt_WithRandomInputs_MatchesBouncyCastle()
    {
        byte[] key = GenerateRandom(32);
        byte[] nonce = GenerateRandom(12); // RFC 7539 (96-bit)
        byte[] plaintext = GenerateRandom(1024);
        byte[] nalixCipher = new byte[1024];

        // Nalix
        var nalix = new ChaCha20(key, nonce, 0);
        nalix.Encrypt(plaintext, nalixCipher);

        // BouncyCastle
        var bc = new ChaCha7539Engine();
        bc.Init(true, new ParametersWithIV(new KeyParameter(key), nonce));
        byte[] bcCipher = new byte[1024];
        bc.ProcessBytes(plaintext, 0, plaintext.Length, bcCipher, 0);

        Assert.Equal(bcCipher, nalixCipher);
    }
}
