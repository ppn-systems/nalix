// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Engine;

namespace Nalix.Codec.Tests.Security;

public sealed class EnvelopeCipherTests
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

    private static int EnvelopeSize(CipherSuiteType suite, int plaintextLength)
        => EnvelopeCipher.HeaderSize
           + EnvelopeCipher.GetNonceLength(suite)
           + plaintextLength
           + EnvelopeCipher.GetTagLength(suite);

    [Theory]
    [InlineData(CipherSuiteType.Chacha20, 12, 0)]
    [InlineData(CipherSuiteType.Salsa20, 8, 0)]
    [InlineData(CipherSuiteType.Chacha20Poly1305, 12, 16)]
    [InlineData(CipherSuiteType.Salsa20Poly1305, 8, 16)]
    public void GetNonceLength_WithEachSuite_ReturnsExpectedNonceAndTagLengths(CipherSuiteType suite, int expectedNonceLength, int expectedTagLength)
    {
        Assert.Equal(expectedNonceLength, EnvelopeCipher.GetNonceLength(suite));
        Assert.Equal(expectedTagLength, EnvelopeCipher.GetTagLength(suite));
    }

    [Fact]
    public void GetNonceLength_WithUnsupportedSuite_ThrowsCipherException()
    {
        CipherSuiteType invalid = (CipherSuiteType)255;

        _ = Assert.ThrowsAny<CipherException>(() => EnvelopeCipher.GetNonceLength(invalid));
        _ = Assert.ThrowsAny<CipherException>(() => EnvelopeCipher.GetTagLength(invalid));
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20)]
    [InlineData(CipherSuiteType.Salsa20)]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithEachSupportedSuite_RoundTrips(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(137, 20);
        byte[] aad = SequentialBytes(11, 80);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, aad, seq: 77u, suite, out int encWritten);
        EnvelopeCipher.Decrypt(key, new System.ReadOnlySpan<byte>(envelope, 0, encWritten), decrypted, aad, suite, out int decWritten, out _);

        Assert.Equal(envelope.Length, encWritten);
        Assert.Equal(plaintext.Length, decWritten);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WhenExpectedAlgorithmMismatchesEnvelope_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(32, 2);
        byte[] envelope = new byte[EnvelopeSize(CipherSuiteType.Chacha20, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, seq: 1u, CipherSuiteType.Chacha20, out _);

        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, envelope, decrypted, CipherSuiteType.Salsa20, out _, out _));
    }

    [Fact]
    public void Encrypt_WhenOutputBufferIsTooSmall_ThrowsArgumentException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(64, 7);
        byte[] tooSmall = new byte[EnvelopeSize(CipherSuiteType.Chacha20Poly1305, plaintext.Length) - 1];

        _ = Assert.Throws<ArgumentException>(() =>
            EnvelopeCipher.Encrypt(key, plaintext, tooSmall, seq: null, CipherSuiteType.Chacha20Poly1305, out _));
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Decrypt_WhenCiphertextByteIsTampered_ThrowsCipherException(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(32, 2);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, seq: 5u, suite, out int written);
        envelope[written - 1] ^= 0xFF; // flip last byte (part of the tag)

        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new System.ReadOnlySpan<byte>(envelope, 0, written), decrypted, suite, out _, out _));
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Decrypt_WithWrongKey_ThrowsCipherException(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] wrongKey = SequentialBytes(32, 200);
        byte[] plaintext = SequentialBytes(32, 2);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, seq: 5u, suite, out int written);

        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(wrongKey, new System.ReadOnlySpan<byte>(envelope, 0, written), decrypted, suite, out _, out _));
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Decrypt_WhenAadIsTampered_ThrowsCipherException(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(32, 2);
        byte[] aad = SequentialBytes(11, 80);
        byte[] wrongAad = SequentialBytes(11, 81);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, aad, seq: 5u, suite, out int written);

        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new System.ReadOnlySpan<byte>(envelope, 0, written), decrypted, wrongAad, suite, out _, out _));
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Decrypt_WhenEnvelopeIsTruncated_ThrowsCipherException(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(32, 2);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, seq: 5u, suite, out int written);
        byte[] truncated = new System.ReadOnlySpan<byte>(envelope, 0, written - 1).ToArray();

        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, truncated, decrypted, suite, out _, out _));
    }

    [Fact]
    public void EncryptDecrypt_WithAadAndChaCha20Poly1305_RoundTripsLength()
    {
        var key = new byte[32];
        var plaintext = new byte[100];
        var aad = new byte[] { 1, 2, 3, 4 };
        var ciphertext = new byte[1000];

        EnvelopeCipher.Encrypt(key, plaintext, ciphertext, aad, null, CipherSuiteType.Chacha20Poly1305, out int written);

        var decrypted = new byte[100];
        EnvelopeCipher.Decrypt(key, ciphertext.AsSpan()[..written], decrypted, aad, CipherSuiteType.Chacha20Poly1305, out int decWritten, out _);

        Assert.Equal(100, decWritten);
    }
}
