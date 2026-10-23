// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Engine;

namespace Nalix.Codec.Tests.Security.Aead;

/// <summary>
/// Round-trip, tamper and misuse scenarios for the detached <see cref="Nalix.Codec.Security.Aead.ChaCha20Poly1305"/> API.
/// </summary>
public sealed class ChaCha20Poly1305RoundTripTests
{
    private const int TagSize = 16;

    private static readonly byte[] s_key32 = new byte[32];

    private static readonly byte[] s_nonce12 = new byte[12];

    private static readonly byte[] s_plaintextShort = System.Text.Encoding.UTF8.GetBytes("Hello, Nalix AEAD!");

    private static readonly byte[] s_plaintextOneBlock = new byte[64];

    private static readonly byte[] s_plaintextMulti = new byte[200];

    private static readonly byte[] s_aad = System.Text.Encoding.UTF8.GetBytes("nalix-aad-header");

    static ChaCha20Poly1305RoundTripTests()
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

    [Fact]
    public void Encrypt_WithShortPlaintext_ProducesCiphertextAndTag()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];

        int written = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextShort, s_aad, ct, tag);

        Assert.Equal(s_plaintextShort.Length, written);
        Assert.NotEqual(new byte[TagSize], tag);
        Assert.NotEqual(s_plaintextShort, ct);
    }

    [Fact]
    public void EncryptDecrypt_WithShortPlaintext_RoundTrips()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextShort, s_aad, ct, tag);
        int ok = ChaCha20Poly1305.Decrypt(s_key32, s_nonce12, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextShort, pt);
    }

    [Fact]
    public void EncryptDecrypt_WithOneBlockPlaintext_RoundTrips()
    {
        byte[] ct = new byte[s_plaintextOneBlock.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextOneBlock.Length];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextOneBlock, s_aad, ct, tag);
        int ok = ChaCha20Poly1305.Decrypt(s_key32, s_nonce12, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextOneBlock, pt);
    }

    [Fact]
    public void EncryptDecrypt_WithMultiBlockPlaintext_RoundTrips()
    {
        byte[] ct = new byte[s_plaintextMulti.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextMulti.Length];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextMulti, s_aad, ct, tag);
        int ok = ChaCha20Poly1305.Decrypt(s_key32, s_nonce12, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextMulti, pt);
    }

    [Fact]
    public void EncryptDecrypt_WithEmptyAad_RoundTrips()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextShort, [], ct, tag);
        int ok = ChaCha20Poly1305.Decrypt(s_key32, s_nonce12, ct, [], tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextShort, pt);
    }

    [Fact]
    public void EncryptDecrypt_WithEmptyPlaintext_RoundTrips()
    {
        byte[] ct = [];
        byte[] tag = new byte[TagSize];
        byte[] pt = [];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, [], s_aad, ct, tag);
        int ok = ChaCha20Poly1305.Decrypt(s_key32, s_nonce12, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
    }

    [Fact]
    public void Decrypt_WhenCiphertextIsTampered_ReturnsMinusOne()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextShort, s_aad, ct, tag);
        ct[0] ^= 0xFF;

        int ok = ChaCha20Poly1305.Decrypt(s_key32, s_nonce12, ct, s_aad, tag, pt);
        Assert.Equal(-1, ok);
    }

    [Fact]
    public void Decrypt_WhenTagIsTampered_ReturnsMinusOne()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextShort, s_aad, ct, tag);
        tag[0] ^= 0x01;

        int ok = ChaCha20Poly1305.Decrypt(s_key32, s_nonce12, ct, s_aad, tag, pt);
        Assert.Equal(-1, ok);
    }

    [Fact]
    public void Decrypt_WithWrongAad_ReturnsMinusOne()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextShort, s_aad, ct, tag);

        int ok = ChaCha20Poly1305.Decrypt(s_key32, s_nonce12, ct,
            System.Text.Encoding.UTF8.GetBytes("wrong-aad"), tag, pt);
        Assert.Equal(-1, ok);
    }

    [Fact]
    public void Encrypt_WithDifferentNonces_ProducesDifferentCiphertextAndTag()
    {
        byte[] ct1 = new byte[s_plaintextShort.Length];
        byte[] tag1 = new byte[TagSize];
        byte[] ct2 = new byte[s_plaintextShort.Length];
        byte[] tag2 = new byte[TagSize];
        byte[] nonce2 = new byte[12]; nonce2[0] = 1;

        _ = ChaCha20Poly1305.Encrypt(s_key32, s_nonce12, s_plaintextShort, s_aad, ct1, tag1);
        _ = ChaCha20Poly1305.Encrypt(s_key32, nonce2, s_plaintextShort, s_aad, ct2, tag2);

        Assert.NotEqual(ct1, ct2);
        Assert.NotEqual(tag1, tag2);
    }

    [Fact]
    public void Encrypt_WithWrongKeyLength_ThrowsException()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];

        _ = Assert.ThrowsAny<Exception>(() =>
            ChaCha20Poly1305.Encrypt(new byte[16], s_nonce12, s_plaintextShort, s_aad, ct, tag));
    }

    [Fact]
    public void Encrypt_WithWrongNonceLength_ThrowsException()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];

        _ = Assert.ThrowsAny<Exception>(() =>
            ChaCha20Poly1305.Encrypt(s_key32, new byte[8], s_plaintextShort, s_aad, ct, tag));
    }
}
