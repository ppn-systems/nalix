// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Security.Symmetric;
using Xunit;

namespace Nalix.Framework.Tests.Cryptography;

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

    [Fact]
    public void EncryptThenDecryptAcrossMultipleBlocksRoundTrips()
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
    public void ConstructorWithInvalidKeyLengthThrowsCipherException()
    {
        byte[] invalidKey = new byte[ChaCha20.KeySize - 1];
        byte[] nonce = new byte[ChaCha20.NonceSize];

        _ = Assert.ThrowsAny<CipherException>(() => new ChaCha20(invalidKey, nonce, 0u));
    }

    [Fact]
    public void ConstructorWithInvalidNonceLengthThrowsCipherException()
    {
        byte[] key = new byte[ChaCha20.KeySize];
        byte[] invalidNonce = new byte[ChaCha20.NonceSize - 1];

        _ = Assert.ThrowsAny<CipherException>(() => new ChaCha20(key, invalidNonce, 0u));
    }

    [Fact]
    public void EncryptWhenDestinationIsTooSmallThrowsCipherException()
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
    public void OperationsAfterClearThrowObjectDisposedException()
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
    public void GenerateKeyBlockAtCounterMaxValueThrowsCipherException()
    {
        byte[] key = SequentialBytes(ChaCha20.KeySize);
        byte[] nonce = SequentialBytes(ChaCha20.NonceSize);
        byte[] block = new byte[ChaCha20.BlockSize];

        ChaCha20 cipher = new(key, nonce, uint.MaxValue);

        _ = Assert.ThrowsAny<CipherException>(() => cipher.GenerateKeyBlock(block));
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

    /// <summary>
    /// RFC 8439 Appendix A.1, Test Vector #1: all-zero key/nonce, block counter 0.
    /// </summary>
    [Fact]
    public void GenerateKeyBlockMatchesRfc8439AppendixA1TestVector1()
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
    public void GenerateKeyBlockMatchesRfc8439AppendixA1TestVector2()
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
    public void GenerateKeyBlockMatchesRfc8439AppendixA1TestVector3()
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
    public void GenerateKeyBlockMatchesRfc8439AppendixA1TestVector4()
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
    public void GenerateKeyBlockMatchesRfc8439AppendixA1TestVector5()
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
    public void EncryptMatchesRfc8439AppendixA2TestVector1()
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
    public void EncryptMatchesRfc8439AppendixA2TestVector3()
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
    public void EncryptInOneCallMatchesEncryptBlockByBlock()
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
}















