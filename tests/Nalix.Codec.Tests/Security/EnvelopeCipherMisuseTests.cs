// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;

namespace Nalix.Codec.Tests.Security;

/// <summary>
/// Adversarial/misuse tests for the envelope wire format. Header field offsets below
/// are taken from the documented 12-byte layout in
/// <c>src/Nalix.Codec/Security/Internal/EnvelopeHeader.cs</c>:
/// [0..3] magic, [4] version, [5] type, [6] flags, [7] nonceLen, [8..11] seq (LE).
/// Header mutation is done by flipping raw bytes at these documented offsets in an
/// envelope produced by the public <see cref="Nalix.Codec.Security.EnvelopeCipher"/> API.
/// </summary>
public sealed class EnvelopeCipherMisuseTests
{
    private const int MagicOffset = 0;

    private const int VersionOffset = 4;

    private const int TypeOffset = 5;

    private const int FlagsOffset = 6;

    private const int NonceLenOffset = 7;

    private const int SeqOffset = 8;

    private static byte[] SequentialBytes(int length, int start = 0)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(start + i);
        }

        return data;
    }

    private static byte[] BuildEnvelope(CipherSuiteType suite, byte[] key, byte[] plaintext, out int written)
    {
        int size = EnvelopeCipher.HeaderSize + EnvelopeCipher.GetNonceLength(suite)
                   + plaintext.Length + EnvelopeCipher.GetTagLength(suite);
        byte[] envelope = new byte[size];
        EnvelopeCipher.Encrypt(key, plaintext, envelope, seq: 42u, suite, out written);
        return envelope;
    }

    // ---- Header field mutation sweep ----

    [Fact]
    public void Decrypt_WhenMagicBytesAreMutated_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20Poly1305, key, plaintext, out int written);
        envelope[MagicOffset] ^= 0xFF;

        byte[] decrypted = new byte[plaintext.Length];
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20Poly1305, out _, out _));
    }

    [Fact]
    public void Decrypt_WhenVersionByteIsMutated_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20Poly1305, key, plaintext, out int written);
        envelope[VersionOffset] = 0xFE; // any version != current (1)

        byte[] decrypted = new byte[plaintext.Length];
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20Poly1305, out _, out _));
    }

    [Fact]
    public void Decrypt_WhenTypeByteIsUndefinedSuite_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20Poly1305, key, plaintext, out int written);
        envelope[TypeOffset] = 0xFA; // not a defined CipherSuiteType byte value

        byte[] decrypted = new byte[plaintext.Length];
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20Poly1305, out _, out _));
    }

    [Fact]
    public void Decrypt_WhenTypeByteIsDifferentValidSuite_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20Poly1305, key, plaintext, out int written);
        envelope[TypeOffset] = (byte)CipherSuiteType.Salsa20Poly1305;

        byte[] decrypted = new byte[plaintext.Length];
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20Poly1305, out _, out _));
    }

    [Fact]
    public void Decrypt_WhenFlagsByteIsMutated_ThrowsCipherException()
    {
        // FLAGS is documented "reserved" and not validated by EnvelopeHeader.Decode; it is,
        // however, covered by the AEAD tag (part of the header AAD), so mutating it must
        // still be caught by authentication.
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20Poly1305, key, plaintext, out int written);
        envelope[FlagsOffset] ^= 0xFF;

        byte[] decrypted = new byte[plaintext.Length];
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20Poly1305, out _, out _));
    }

    [Fact]
    public void Decrypt_WhenNonceLengthIsZero_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20Poly1305, key, plaintext, out int written);
        envelope[NonceLenOffset] = 0;

        byte[] decrypted = new byte[plaintext.Length];
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20Poly1305, out _, out _));
    }

    [Fact]
    public void Decrypt_WhenNonceLengthIsMaxByte_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20Poly1305, key, plaintext, out int written);
        envelope[NonceLenOffset] = 0xFF; // int.MaxValue-as-byte analogue: max representable in this 1-byte field

        byte[] decrypted = new byte[plaintext.Length];
        // Must throw cleanly (envelope too short for claimed nonce length), never read OOB / crash.
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20Poly1305, out _, out _));
    }

    [Fact]
    public void Decrypt_WhenSequenceIsMutatedForAeadSuite_ThrowsCipherException()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20Poly1305, key, plaintext, out int written);
        envelope[SeqOffset] ^= 0xFF;

        byte[] decrypted = new byte[plaintext.Length];
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20Poly1305, out _, out _));
    }

    /// <summary>
    /// Documents current (non-AEAD) behavior: for stream/CTR suites there is no authentication
    /// tag, so a mutated SEQ field is not detected as an error — it silently changes the
    /// effective keystream (SEQ is XORed into the nonce), producing garbage plaintext instead
    /// of an exception. This is a behavioral finding, not a crash/OOB bug.
    /// </summary>
    [Fact]
    public void Decrypt_WhenSequenceIsMutatedForStreamSuite_ChangesPlaintextWithoutThrowing()
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(16, 2);
        byte[] envelope = BuildEnvelope(CipherSuiteType.Chacha20, key, plaintext, out int written);
        envelope[SeqOffset] ^= 0xFF;

        byte[] decrypted = new byte[plaintext.Length];
        EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, CipherSuiteType.Chacha20, out int decWritten, out _);

        Assert.Equal(plaintext.Length, decWritten);
        Assert.NotEqual(plaintext, decrypted); // corrupted, not equal to original — but no exception
    }

    // ---- Truncation sweep: every length from 0 to full ----

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void TryDecrypt_WhenEnvelopeIsTruncatedAtAnyLength_NeverSucceeds(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 1);
        byte[] plaintext = SequentialBytes(24, 2);
        byte[] envelope = BuildEnvelope(suite, key, plaintext, out int written);
        byte[] decrypted = new byte[plaintext.Length];

        for (int len = 0; len < written; len++)
        {
            bool ok;
            try
            {
                ok = EnvelopeCipher.TryDecrypt(key, new ReadOnlySpan<byte>(envelope, 0, len), decrypted, suite, out _, out _);
            }
            catch (CipherException)
            {
                ok = false;
            }

            Assert.False(ok, $"Truncated envelope of length {len} (full length {written}) must not decrypt successfully.");
        }

        // The full, untruncated envelope must still succeed.
        Assert.True(EnvelopeCipher.TryDecrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, suite, out _, out _));
    }

    // ---- Seeded-random garbage fuzzing ----

    /// <summary>
    /// Feeds 500 seeded-random garbage byte arrays of random length to envelope decrypt.
    /// Only a documented failure path (TryDecrypt returning false, or a thrown
    /// <see cref="CipherException"/>/<see cref="ArgumentException"/>) is acceptable — no crash,
    /// hang, or partial/garbage plaintext write beyond the returned `written` count.
    /// </summary>
    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void TryDecrypt_WithRandomGarbageEnvelopes_NeverThrowsUndocumentedException(CipherSuiteType suite)
    {
        const int seed = 20260704;
        System.Random rng = new(seed);
        byte[] key = SequentialBytes(32, 3);
        byte[] decrypted = new byte[8192];

        for (int i = 0; i < 500; i++)
        {
            int len = rng.Next(0, 512);
            byte[] garbage = new byte[len];
            rng.NextBytes(garbage);

            try
            {
                bool ok = EnvelopeCipher.TryDecrypt(key, garbage, decrypted, suite, out int w, out _);
                if (ok)
                {
                    // Extremely unlikely (would require a valid-looking magic+version+type+auth by chance);
                    // if it ever happens it is not itself a bug, just record it did not throw.
                    Assert.True(w <= decrypted.Length, $"seed={seed} iteration={i} len={len} garbage={Convert.ToHexString(garbage)}: written exceeds destination.");
                }
            }
            catch (Exception ex) when (ex is CipherException or ArgumentException)
            {
                // documented failure types — acceptable
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed={seed} iteration={i} len={len} garbage={Convert.ToHexString(garbage)}: undocumented exception type {ex.GetType()}: {ex.Message}");
            }
        }
    }

    // ---- Tag-position proof ----

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void Decrypt_WhenOnlyFinalTagBytesAreCorrupted_ThrowsCipherException(CipherSuiteType suite)
    {
        byte[] key = SequentialBytes(32, 4);
        byte[] plaintext = SequentialBytes(40, 5);
        byte[] envelope = BuildEnvelope(suite, key, plaintext, out int written);
        int tagLen = EnvelopeCipher.GetTagLength(suite);

        for (int i = 0; i < tagLen; i++)
        {
            envelope[written - tagLen + i] ^= 0xFF;
        }

        byte[] decrypted = new byte[plaintext.Length];
        _ = Assert.ThrowsAny<CipherException>(() =>
            EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, suite, out _, out _));
    }
}
