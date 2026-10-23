// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Engine;

namespace Nalix.Codec.Tests.Security.Aead;

/// <summary>
/// Round-trip, tamper and misuse scenarios for the detached <see cref="Nalix.Codec.Security.Aead.Salsa20Poly1305"/> API.
/// </summary>
public sealed class Salsa20Poly1305RoundTripTests
{
    private const int TagSize = 16;

    private static readonly byte[] s_key32 = new byte[32];

    private static readonly byte[] s_key16 = new byte[16];

    private static readonly byte[] s_nonce8 = new byte[8];

    private static readonly byte[] s_plaintextShort = System.Text.Encoding.UTF8.GetBytes("Hello, Nalix AEAD!");

    private static readonly byte[] s_plaintextOneBlock = new byte[64];

    private static readonly byte[] s_plaintextMulti = new byte[200];

    private static readonly byte[] s_aad = System.Text.Encoding.UTF8.GetBytes("nalix-aad-header");

    static Salsa20Poly1305RoundTripTests()
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

        int written = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextShort, s_aad, ct, tag);

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

        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextShort, s_aad, ct, tag);
        int ok = Salsa20Poly1305.Decrypt(s_key32, s_nonce8, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextShort, pt);
    }

    [Fact]
    public void EncryptDecrypt_WithOneBlockPlaintext_RoundTrips()
    {
        byte[] ct = new byte[s_plaintextOneBlock.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextOneBlock.Length];

        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextOneBlock, s_aad, ct, tag);
        int ok = Salsa20Poly1305.Decrypt(s_key32, s_nonce8, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextOneBlock, pt);
    }

    [Fact]
    public void EncryptDecrypt_WithMultiBlockPlaintext_RoundTrips()
    {
        byte[] ct = new byte[s_plaintextMulti.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextMulti.Length];

        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextMulti, s_aad, ct, tag);
        int ok = Salsa20Poly1305.Decrypt(s_key32, s_nonce8, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextMulti, pt);
    }

    [Fact]
    public void EncryptDecrypt_With128BitKey_RoundTrips()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = Salsa20Poly1305.Encrypt(s_key16, s_nonce8, s_plaintextShort, s_aad, ct, tag);
        int ok = Salsa20Poly1305.Decrypt(s_key16, s_nonce8, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextShort, pt);
    }

    [Fact]
    public void EncryptDecrypt_WithEmptyAad_RoundTrips()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextShort, [], ct, tag);
        int ok = Salsa20Poly1305.Decrypt(s_key32, s_nonce8, ct, [], tag, pt);

        Assert.True(ok >= 0);
        Assert.Equal(s_plaintextShort, pt);
    }

    [Fact]
    public void EncryptDecrypt_WithEmptyPlaintext_RoundTrips()
    {
        byte[] ct = [];
        byte[] tag = new byte[TagSize];
        byte[] pt = [];

        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, [], s_aad, ct, tag);
        int ok = Salsa20Poly1305.Decrypt(s_key32, s_nonce8, ct, s_aad, tag, pt);

        Assert.True(ok >= 0);
    }

    [Fact]
    public void Decrypt_WhenCiphertextIsTampered_ReturnsMinusOne()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextShort, s_aad, ct, tag);
        ct[0] ^= 0xFF;

        int ok = Salsa20Poly1305.Decrypt(s_key32, s_nonce8, ct, s_aad, tag, pt);
        Assert.Equal(-1, ok);
    }

    [Fact]
    public void Decrypt_WhenTagIsTampered_ReturnsMinusOne()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextShort, s_aad, ct, tag);
        tag[7] ^= 0x01;

        int ok = Salsa20Poly1305.Decrypt(s_key32, s_nonce8, ct, s_aad, tag, pt);
        Assert.Equal(-1, ok);
    }

    [Fact]
    public void Decrypt_WithWrongAad_ReturnsMinusOne()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];
        byte[] pt = new byte[s_plaintextShort.Length];

        _ = Salsa20Poly1305.Encrypt(s_key32, s_nonce8, s_plaintextShort, s_aad, ct, tag);

        int ok = Salsa20Poly1305.Decrypt(s_key32, s_nonce8, ct,
            System.Text.Encoding.UTF8.GetBytes("wrong-aad"), tag, pt);
        Assert.Equal(-1, ok);
    }

    [Fact]
    public void Encrypt_WithWrongKeyLength_ThrowsException()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];

        _ = Assert.ThrowsAny<Exception>(() =>
            Salsa20Poly1305.Encrypt(new byte[20], s_nonce8, s_plaintextShort, s_aad, ct, tag));
    }

    [Fact]
    public void Encrypt_WithWrongNonceLength_ThrowsException()
    {
        byte[] ct = new byte[s_plaintextShort.Length];
        byte[] tag = new byte[TagSize];

        _ = Assert.ThrowsAny<Exception>(() =>
            Salsa20Poly1305.Encrypt(s_key32, new byte[12], s_plaintextShort, s_aad, ct, tag));
    }
}
