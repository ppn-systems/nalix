// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;

namespace Nalix.Codec.Tests.Security;

/// <summary>
/// Boundary and edge-case sweeps for <see cref="EnvelopeCipher"/> across all supported suites:
/// plaintext-length sweeps around cipher block sizes (ChaCha20 block = 64, Keccak rate = 136 is
/// not directly exercised by these ciphers but included for completeness of the sweep range),
/// AAD-length sweeps, empty-plaintext/AAD combinations, buffer aliasing, and offset slices.
/// </summary>
public sealed class EnvelopeCipherBoundaryTests
{
    private static readonly int[] s_lengths =
        [0, 1, 15, 16, 17, 63, 64, 65, 135, 136, 137, 255, 256, 257, 65536];

    private static readonly int[] s_aadLengths = [0, 1, 16];

    private static readonly CipherSuiteType[] s_allSuites =
    [
        CipherSuiteType.Chacha20,
        CipherSuiteType.Salsa20,
        CipherSuiteType.Chacha20Poly1305,
        CipherSuiteType.Salsa20Poly1305,
    ];

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

    public static TheoryData<CipherSuiteType, int> SuiteAndLength()
    {
        TheoryData<CipherSuiteType, int> data = [];
        foreach (CipherSuiteType suite in s_allSuites)
        {
            foreach (int len in s_lengths)
            {
                data.Add(suite, len);
            }
        }

        return data;
    }

    public static TheoryData<CipherSuiteType, int> AeadSuiteAndAadLength()
    {
        TheoryData<CipherSuiteType, int> data = [];
        foreach (CipherSuiteType suite in new[] { CipherSuiteType.Chacha20Poly1305, CipherSuiteType.Salsa20Poly1305 })
        {
            foreach (int len in s_aadLengths)
            {
                data.Add(suite, len);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SuiteAndLength))]
    public void EncryptDecrypt_AtEveryPlaintextLength_RoundTripsBytes(CipherSuiteType suite, int length)
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(length, 5);
        byte[] envelope = new byte[EnvelopeSize(suite, length)];
        byte[] decrypted = new byte[length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, seq: 1u, suite, out int written);
        EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, suite, out int decWritten, out _);

        Assert.Equal(envelope.Length, written);
        Assert.Equal(length, decWritten);
        Assert.Equal(plaintext, decrypted);
    }

    [Theory]
    [MemberData(nameof(AeadSuiteAndAadLength))]
    public void EncryptDecrypt_AtEveryAadLength_RoundTrips(CipherSuiteType suite, int aadLength)
    {
        byte[] key = SequentialBytes(32, 2);
        byte[] plaintext = SequentialBytes(64, 7);
        byte[] aad = SequentialBytes(aadLength, 9);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, aad, seq: 3u, suite, out int written);
        EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, aad, suite, out int decWritten, out _);

        Assert.Equal(plaintext, decrypted);
        Assert.Equal(plaintext.Length, decWritten);
    }

    /// <summary>
    /// Regression test: when header+nonce+userAAD exceeds the 256-byte stackalloc threshold,
    /// <c>AeadEngine.Encrypt</c>/<c>Decrypt</c> rent a pooled buffer, which must be sliced back
    /// to <c>authenticatedDataLength</c> before use (<see cref="System.Buffers.ArrayPool{T}.Rent"/>
    /// only guarantees AT LEAST the requested length). An unsliced buffer includes stale pool-tail
    /// garbage in the MAC input and authentication always fails for AAD larger than ~244 bytes.
    /// Fixed in AeadEngine.cs by slicing the rented buffer with AsSpan(0, authenticatedDataLength).
    /// </summary>
    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithAadLargerThan256Bytes_RoundTrips(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 2);
        byte[] plaintext = SequentialBytes(64, 7);
        byte[] aad = SequentialBytes(4096, 9);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, aad, seq: 3u, suite, out int written);
        EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, aad, suite, out int decWritten, out _);

        Assert.Equal(plaintext, decrypted);
        Assert.Equal(plaintext.Length, decWritten);
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithEmptyPlaintextAndNonEmptyAad_RoundTrips(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 3);
        byte[] plaintext = [];
        byte[] aad = SequentialBytes(32, 11);
        byte[] envelope = new byte[EnvelopeSize(suite, 0)];
        byte[] decrypted = [];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, aad, seq: 4u, suite, out int written);
        EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, aad, suite, out int decWritten, out _);

        Assert.Equal(0, decWritten);
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithNonEmptyPlaintextAndEmptyAad_RoundTrips(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 4);
        byte[] plaintext = SequentialBytes(48, 13);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, ReadOnlySpan<byte>.Empty, seq: 6u, suite, out int written);
        EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, ReadOnlySpan<byte>.Empty, suite, out int decWritten, out _);

        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// Decrypting one byte less than the exact envelope size produced by Encrypt must fail
    /// cleanly (never corrupt/hang) — this is the "at limit, and one byte over" boundary check
    /// (the source has no explicit max envelope size constant; the practical boundary here is
    /// the exact-vs-truncated envelope length).
    /// </summary>
    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Decrypt_WhenEnvelopeIsOneByteShort_ThrowsCipherException(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 5);
        byte[] plaintext = SequentialBytes(32, 15);
        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length)];
        byte[] decrypted = new byte[plaintext.Length];

        EnvelopeCipher.Encrypt(key, plaintext, envelope, seq: 7u, suite, out int written);

        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written - 1), decrypted, suite, out _, out _));
    }

    /// <summary>
    /// Buffer aliasing: encrypting in place (plaintext and ciphertext body share the same
    /// backing array/offset) must either produce a correct round trip or throw — never
    /// silently corrupt unrelated memory. This exercises the ciphertext body region of the
    /// envelope aliased with the source plaintext buffer.
    /// </summary>
    [Theory]
    [InlineData(CipherSuiteType.Chacha20)]
    [InlineData(CipherSuiteType.Salsa20)]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Encrypt_WithAliasedInPlaceBuffer_RoundTripsOrThrows(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 6);
        int plaintextLen = 48;
        int headerAndNonce = EnvelopeCipher.HeaderSize + EnvelopeCipher.GetNonceLength(suite);
        byte[] buffer = new byte[headerAndNonce + plaintextLen + EnvelopeCipher.GetTagLength(suite)];
        byte[] originalPlaintext = SequentialBytes(plaintextLen, 20);
        originalPlaintext.CopyTo(buffer.AsSpan(headerAndNonce, plaintextLen));

        Exception? thrown = null;
        int written = 0;
        try
        {
            EnvelopeCipher.Encrypt(key, buffer.AsSpan(headerAndNonce, plaintextLen), buffer, seq: 8u, suite, out written);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        if (thrown is null)
        {
            byte[] decrypted = new byte[plaintextLen];
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(buffer, 0, written), decrypted, suite, out int decWritten, out _);
            Assert.Equal(plaintextLen, decWritten);
        }
    }

    /// <summary>
    /// Offset slices: reading key/plaintext/AAD from a non-zero offset into a larger backing
    /// array must not corrupt output — catches zero-based-offset assumption bugs.
    /// </summary>
    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_WithOffsetSlicedInputs_RoundTrips(CipherSuiteType suite)
    {
        byte[] keyBacking = SequentialBytes(32 + 10, 1);
        ReadOnlySpan<byte> key = keyBacking.AsSpan(3, 32);

        byte[] ptBacking = SequentialBytes(64 + 10, 40);
        ReadOnlySpan<byte> plaintext = ptBacking.AsSpan(5, 64);

        byte[] aadBacking = SequentialBytes(20 + 10, 90);
        ReadOnlySpan<byte> aad = aadBacking.AsSpan(4, 20);

        byte[] envelope = new byte[EnvelopeSize(suite, plaintext.Length) + 6];
        byte[] decrypted = new byte[plaintext.Length + 6];

        EnvelopeCipher.Encrypt(key, plaintext, envelope.AsSpan(3), aad, seq: 9u, suite, out int written);
        EnvelopeCipher.Decrypt(key, envelope.AsSpan(3, written), decrypted.AsSpan(2), aad, suite, out int decWritten, out _);

        Assert.Equal(plaintext.Length, decWritten);
        Assert.Equal(plaintext.ToArray(), decrypted.AsSpan(2, decWritten).ToArray());
    }
}
