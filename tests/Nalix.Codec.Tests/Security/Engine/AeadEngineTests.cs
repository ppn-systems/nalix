// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Engine;

namespace Nalix.Codec.Tests.Security.Engine;

/// <summary>
/// Verifies the public envelope API exposed by <see cref="Nalix.Codec.Security.Engine.AeadEngine"/>.
/// </summary>
public sealed class AeadEngineTests
{
    private const int HeaderSize = 12;

    private const int TagSize = 16;

    private const int ChaCha20NonceLen = 12;

    private const int Salsa20NonceLen = 8;

    private static readonly byte[] s_key32 = new byte[32];

    private static readonly byte[] s_nonce12 = new byte[12];

    private static readonly byte[] s_nonce8 = new byte[8];

    private static readonly byte[] s_plaintextShort = System.Text.Encoding.UTF8.GetBytes("Hello, Nalix AEAD!");

    private static readonly byte[] s_plaintextOneBlock = new byte[64];

    private static readonly byte[] s_plaintextMulti = new byte[200];

    private static readonly byte[] s_aad = System.Text.Encoding.UTF8.GetBytes("nalix-aad-header");

    static AeadEngineTests()
    {
        for (int i = 0; i < s_plaintextOneBlock.Length; i++)
        {
            s_plaintextOneBlock[i] = (byte)(i + 1);
        }

        for (int i = 0; i < s_plaintextMulti.Length; i++)
        {
            s_plaintextMulti[i] = (byte)(i & 0xFF);
        }
    }

    private static int EnvelopeSize(int nonceLen, int ptLen)
        => HeaderSize + nonceLen + ptLen + TagSize;

    private static int NonceLen(CipherSuiteType alg)
        => alg is CipherSuiteType.Chacha20Poly1305 ? ChaCha20NonceLen : Salsa20NonceLen;

    private static byte[] NonceFor(CipherSuiteType alg)
        => alg is CipherSuiteType.Chacha20Poly1305 ? s_nonce12 : s_nonce8;

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithShortPlaintext_RoundTrips(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        int nLen = NonceLen(algorithm);
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] plaintext = new byte[s_plaintextShort.Length];

        AeadEngine.Encrypt(
            s_key32, s_plaintextShort, envelope, nonce,
            s_aad, seq: 1u, algorithm, out int encWritten);
        Assert.Equal(EnvelopeSize(nLen, s_plaintextShort.Length), encWritten);

        AeadEngine.Decrypt(
            s_key32, envelope.AsSpan()[..encWritten], plaintext, s_aad, out int decWritten);
        Assert.Equal(s_plaintextShort.Length, decWritten);
        Assert.Equal(s_plaintextShort, plaintext);
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithEmptyPlaintext_RoundTrips(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        int nLen = NonceLen(algorithm);
        byte[] envelope = new byte[EnvelopeSize(nLen, 0)];

        AeadEngine.Encrypt(
            s_key32, [], envelope, nonce,
            s_aad, seq: 0u, algorithm, out int encWritten);
        Assert.Equal(EnvelopeSize(nLen, 0), encWritten);

        AeadEngine.Decrypt(
            s_key32, envelope.AsSpan()[..encWritten], [], s_aad, out int decWritten);
        Assert.Equal(0, decWritten);
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithMultiBlockPlaintext_RoundTrips(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        int nLen = NonceLen(algorithm);
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextMulti.Length)];
        byte[] plaintext = new byte[s_plaintextMulti.Length];

        AeadEngine.Encrypt(
            s_key32, s_plaintextMulti, envelope, nonce,
            s_aad, seq: 99u, algorithm, out int encWritten);

        AeadEngine.Decrypt(
            s_key32, envelope.AsSpan()[..encWritten], plaintext, s_aad, out int decWritten);
        Assert.Equal(s_plaintextMulti.Length, decWritten);
        Assert.Equal(s_plaintextMulti, plaintext);
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithNullSequence_RoundTrips(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        int nLen = NonceLen(algorithm);
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] plaintext = new byte[s_plaintextShort.Length];

        AeadEngine.Encrypt(
            s_key32, s_plaintextShort, envelope, nonce,
            s_aad, seq: null, algorithm, out int encWritten);

        AeadEngine.Decrypt(
            s_key32, envelope.AsSpan()[..encWritten], plaintext, s_aad, out int decWritten);

        Assert.Equal(s_plaintextShort.Length, decWritten);
        Assert.Equal(s_plaintextShort, plaintext);
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Encrypt_WhenBufferIsTooSmall_ThrowsArgumentException(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        byte[] tinyBuffer = new byte[1];

        _ = Assert.Throws<ArgumentException>(() => AeadEngine.Encrypt(
            s_key32, s_plaintextShort, tinyBuffer, nonce,
            s_aad, seq: 0u, algorithm, out _));
    }

    [Fact]
    public void Decrypt_WhenMagicBytesAreCorrupted_ThrowsCipherException()
    {
        int nLen = ChaCha20NonceLen;
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] ptBuf = new byte[s_plaintextShort.Length];

        AeadEngine.Encrypt(s_key32, s_plaintextShort, envelope, s_nonce12,
            s_aad, seq: 1u, CipherSuiteType.Chacha20Poly1305, out _);

        envelope[0] ^= 0xFF;

        _ = Assert.ThrowsAny<CipherException>(() => AeadEngine.Decrypt(s_key32, envelope, ptBuf, s_aad, out _));
    }

    [Fact]
    public void Decrypt_WhenCiphertextIsTampered_ThrowsCipherException()
    {
        int nLen = ChaCha20NonceLen;
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] ptBuf = new byte[s_plaintextShort.Length];

        AeadEngine.Encrypt(s_key32, s_plaintextShort, envelope, s_nonce12,
            s_aad, seq: 1u, CipherSuiteType.Chacha20Poly1305, out int encWritten);

        Assert.True(encWritten > HeaderSize + nLen + TagSize, "Plaintext must be non-empty for this test");

        int ctOffset = HeaderSize + nLen;
        envelope[ctOffset] ^= 0x01;
        _ = Assert.ThrowsAny<CipherException>(() => AeadEngine.Decrypt(
            s_key32, envelope.AsSpan()[..encWritten], ptBuf, s_aad, out _));
    }

    [Fact]
    public void Decrypt_WhenTagIsTampered_ThrowsCipherException()
    {
        int nLen = ChaCha20NonceLen;
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] ptBuf = new byte[s_plaintextShort.Length];

        AeadEngine.Encrypt(s_key32, s_plaintextShort, envelope, s_nonce12,
            s_aad, seq: 5u, CipherSuiteType.Chacha20Poly1305, out int encWritten);
        Assert.True(encWritten >= HeaderSize + nLen + s_plaintextShort.Length + TagSize, "Envelope length must include tag");

        envelope[encWritten - 1] ^= 0xFF;

        _ = Assert.ThrowsAny<CipherException>(() => AeadEngine.Decrypt(
            s_key32, envelope.AsSpan()[..encWritten], ptBuf, s_aad, out _));
    }

    [Fact]
    public void Decrypt_WithWrongAad_ThrowsCipherException()
    {
        int nLen = ChaCha20NonceLen;
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] ptBuf = new byte[s_plaintextShort.Length];

        AeadEngine.Encrypt(s_key32, s_plaintextShort, envelope, s_nonce12,
            s_aad, seq: 3u, CipherSuiteType.Chacha20Poly1305, out int encWritten);

        _ = Assert.ThrowsAny<CipherException>(() => AeadEngine.Decrypt(
            s_key32, envelope.AsSpan()[..encWritten], ptBuf,
            System.Text.Encoding.UTF8.GetBytes("wrong-aad"), out _));
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Decrypt_WhenSequenceHeaderIsTampered_ThrowsCipherException(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        int nLen = NonceLen(algorithm);
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] plaintext = new byte[s_plaintextShort.Length];

        AeadEngine.Encrypt(
            s_key32, s_plaintextShort, envelope, nonce,
            s_aad, seq: 0x01020304u, algorithm, out int written);

        envelope[8] ^= 0xFF;

        _ = Assert.ThrowsAny<CipherException>(() =>
            AeadEngine.Decrypt(s_key32, envelope.AsSpan()[..written], plaintext, s_aad, out _));
    }

    [Fact]
    public void Decrypt_WhenEnvelopeIsEmpty_ThrowsCipherException()
    {
        byte[] ptBuf = new byte[10];
        _ = Assert.ThrowsAny<CipherException>(() => AeadEngine.Decrypt(s_key32, [], ptBuf, s_aad, out _));
    }

    [Fact]
    public void Decrypt_WhenEnvelopeIsTruncatedToHeader_ThrowsCipherException()
    {
        int nLen = ChaCha20NonceLen;
        byte[] envelope = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] ptBuf = new byte[s_plaintextShort.Length];

        AeadEngine.Encrypt(s_key32, s_plaintextShort, envelope, s_nonce12,
            s_aad, seq: 2u, CipherSuiteType.Chacha20Poly1305, out _);

        _ = Assert.ThrowsAny<CipherException>(() => AeadEngine.Decrypt(
            s_key32, envelope.AsSpan()[..HeaderSize], ptBuf, s_aad, out _));
    }

    [Fact]
    public void Encrypt_WithChaChaAndSalsaPrimitives_ProducesDifferentOutput()
    {
        byte[] ct1 = new byte[s_plaintextShort.Length];
        byte[] tag1 = new byte[TagSize];
        byte[] ct2 = new byte[s_plaintextShort.Length];
        byte[] tag2 = new byte[TagSize];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextShort, s_aad, ct1, tag1);
        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextShort, s_aad, ct2, tag2);

        Assert.NotEqual(ct1, ct2);
        Assert.NotEqual(tag1, tag2);
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Encrypt_WithDifferentKeys_ProducesDifferentCiphertext(CipherSuiteType algorithm)
    {
        byte[] nonce = NonceFor(algorithm);
        int nLen = NonceLen(algorithm);
        int ctStart = HeaderSize + nLen;

        byte[] env1 = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] env2 = new byte[EnvelopeSize(nLen, s_plaintextShort.Length)];
        byte[] key2 = new byte[32]; key2[0] = 0xAB;

        AeadEngine.Encrypt(s_key32, s_plaintextShort, env1, nonce, s_aad, seq: 1u, algorithm, out int w1);
        AeadEngine.Encrypt(key2, s_plaintextShort, env2, nonce, s_aad, seq: 1u, algorithm, out int w2);

        byte[] ct1 = env1[ctStart..w1];
        byte[] ct2 = env2[ctStart..w2];

        Assert.NotEqual(ct1, ct2);
    }
}
