// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security.Engine;
using Nalix.Codec.Security.Symmetric;

namespace Nalix.Codec.Tests.Security.Engine;

/// <summary>
/// Verifies the public envelope API exposed by <see cref="Nalix.Codec.Security.Engine.SymmetricEngine"/>.
/// </summary>
public sealed class SymmetricEngineTests
{
    private const int HeaderSize = 12;

    private const int ChaCha20NonceSize = 12;

    private const int Salsa20NonceSize = 8;

    private static readonly byte[] s_key32 = new byte[32];

    private static readonly byte[] s_nonce12 = new byte[12];

    private static readonly byte[] s_nonce8 = new byte[8];

    private static readonly byte[] s_plaintextShort =
        System.Text.Encoding.UTF8.GetBytes("Hello, Nalix!");

    private static readonly byte[] s_plaintextOneBlock = new byte[64];

    private static readonly byte[] s_plaintextMultiBlock = new byte[200];

    static SymmetricEngineTests()
    {
        for (int i = 0; i < s_plaintextOneBlock.Length; i++)
        {
            s_plaintextOneBlock[i] = (byte)(i + 1);
        }

        for (int i = 0; i < s_plaintextMultiBlock.Length; i++)
        {
            s_plaintextMultiBlock[i] = (byte)(i & 0xFF);
        }
    }

    private static int EnvelopeSize(int nonceLen, int plaintextLen)
        => HeaderSize + nonceLen + plaintextLen;

    private static int NonceLength(CipherSuiteType algorithm)
        => algorithm is CipherSuiteType.Chacha20 ? ChaCha20NonceSize : Salsa20NonceSize;

    private static byte[] NonceFor(CipherSuiteType algorithm)
        => algorithm is CipherSuiteType.Chacha20 ? s_nonce12 : s_nonce8;

    [Theory]
    [InlineData(CipherSuiteType.Chacha20)]
    [InlineData(CipherSuiteType.Salsa20)]
    public void EncryptDecrypt_WithMultiBlockPlaintext_RoundTrips(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        int nLen = NonceLength(algorithm);
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextMultiBlock.Length)];
        byte[] plaintext = new byte[s_plaintextMultiBlock.Length];

        SymmetricEngine.Encrypt(
            s_key32, s_plaintextMultiBlock, envelope, nonce,
            seq: 99u, algorithm, out int encWritten);
        Assert.Equal(EnvelopeSize(nLen, s_plaintextMultiBlock.Length), encWritten);

        SymmetricEngine.Decrypt(s_key32, envelope, plaintext, out int decWritten);
        Assert.Equal(s_plaintextMultiBlock.Length, decWritten);
        Assert.Equal(s_plaintextMultiBlock, plaintext);
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20)]
    [InlineData(CipherSuiteType.Salsa20)]
    public void Encrypt_WhenBufferIsTooSmall_ThrowsArgumentException(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        byte[] tinyBuffer = new byte[1];

        _ = Assert.Throws<ArgumentException>(() => SymmetricEngine.Encrypt(
            s_key32, s_plaintextShort, tinyBuffer, nonce,
            seq: 0u, algorithm, out _));
    }

    [Fact]
    public void Decrypt_WhenMagicBytesAreCorrupted_ThrowsCipherException()
    {
        int nLen = ChaCha20NonceSize;
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] ptBuf = new byte[s_plaintextShort.Length];

        SymmetricEngine.Encrypt(
            s_key32, s_plaintextShort, envelope, s_nonce12,
            seq: 1u, CipherSuiteType.Chacha20, out _);

        envelope[0] ^= 0xFF;

        _ = Assert.ThrowsAny<CipherException>(() => SymmetricEngine.Decrypt(s_key32, envelope, ptBuf, out _));
    }

    [Fact]
    public void Decrypt_WhenEnvelopeIsEmpty_ThrowsCipherException()
    {
        byte[] ptBuf = new byte[10];
        _ = Assert.ThrowsAny<CipherException>(() => SymmetricEngine.Decrypt(s_key32, [], ptBuf, out _));
    }

    [Fact]
    public void Decrypt_WhenEnvelopeIsTruncatedToHeader_ThrowsCipherException()
    {
        int nLen = ChaCha20NonceSize;
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] ptBuf = new byte[s_plaintextShort.Length];

        SymmetricEngine.Encrypt(
            s_key32, s_plaintextShort, envelope, s_nonce12,
            seq: 5u, CipherSuiteType.Chacha20, out _);

        byte[] truncated = envelope[..HeaderSize];

        _ = Assert.ThrowsAny<CipherException>(() => SymmetricEngine.Decrypt(s_key32, truncated, ptBuf, out _));
    }
}
