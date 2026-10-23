// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Security.Cryptography;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Asymmetric;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Primitives;
using Nalix.Codec.Security.Symmetric;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using BCX25519 = Org.BouncyCastle.Math.EC.Rfc7748.X25519;

namespace Nalix.Codec.Tests.Security.Asymmetric;

public sealed class X25519Tests
{
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

    /// <summary>
    /// RFC 7748 §5.2, first X25519 scalar-mult test vector.
    /// </summary>
    [Fact]
    public void Agreement_WithRfc7748Section52Vector1_MatchesExpectedSecret()
    {
        Bytes32 scalar = new(HexToBytes("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"));
        Bytes32 point = new(HexToBytes("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"));
        Bytes32 expected = new(HexToBytes("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552"));

        Bytes32 actual = X25519.Agreement(scalar, point);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// RFC 7748 §5.2, second X25519 scalar-mult test vector.
    /// </summary>
    [Fact]
    public void Agreement_WithRfc7748Section52Vector2_MatchesExpectedSecret()
    {
        Bytes32 scalar = new(HexToBytes("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"));
        Bytes32 point = new(HexToBytes("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493"));
        Bytes32 expected = new(HexToBytes("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957"));

        Bytes32 actual = X25519.Agreement(scalar, point);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// RFC 7748 §5.2 iterated test: starting from k = u = 9 (basepoint), after
    /// 1 and 1,000 iterations of k, u = X25519(k, u), k.
    /// </summary>
    [Fact]
    public void Agreement_WithRfc7748IteratedTest1And1000_MatchesExpectedValues()
    {
        byte[] basepoint = new byte[32];
        basepoint[0] = 9;
        Bytes32 k = new(basepoint);
        Bytes32 u = new(basepoint);

        Bytes32 after1 = new(HexToBytes("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079"));
        Bytes32 after1000 = new(HexToBytes("684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51"));

        for (int i = 1; i <= 1000; i++)
        {
            Bytes32 next = X25519.Agreement(k, u);
            u = k;
            k = next;

            if (i == 1)
            {
                Assert.Equal(after1, k);
            }
        }

        Assert.Equal(after1000, k);
    }

    /// <summary>
    /// RFC 7748 §5.2 iterated test at 1,000,000 iterations. Marked Explicit-equivalent
    /// (skipped by default) since it is expensive; run manually to verify.
    /// </summary>
    [Fact(Skip = "Expensive (1,000,000 iterations) — run manually to verify against RFC 7748 §5.2.")]
    public void Agreement_WithRfc7748IteratedTest1000000_MatchesExpectedValue()
    {
        byte[] basepoint = new byte[32];
        basepoint[0] = 9;
        Bytes32 k = new(basepoint);
        Bytes32 u = new(basepoint);
        Bytes32 expected = new(HexToBytes("7c3911e0ab2586fd864497297e575e6f3bc601c0883c30df5f4dd2d24f665424"));

        for (int i = 1; i <= 1_000_000; i++)
        {
            Bytes32 next = X25519.Agreement(k, u);
            u = k;
            k = next;
        }

        Assert.Equal(expected, k);
    }

    /// <summary>
    /// RFC 7748 §6.1 Alice/Bob Diffie-Hellman test vector.
    /// </summary>
    [Fact]
    public void Agreement_WithRfc7748Section61AliceBobVector_MatchesExpectedKeysAndSecret()
    {
        Bytes32 alicePrivate = new(HexToBytes("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"));
        Bytes32 alicePublicExpected = new(HexToBytes("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"));
        Bytes32 bobPrivate = new(HexToBytes("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"));
        Bytes32 bobPublicExpected = new(HexToBytes("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"));
        Bytes32 sharedExpected = new(HexToBytes("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"));

        X25519.X25519KeyPair alice = X25519.GenerateKeyFromPrivateKey(alicePrivate);
        X25519.X25519KeyPair bob = X25519.GenerateKeyFromPrivateKey(bobPrivate);

        Assert.Equal(alicePublicExpected, alice.PublicKey);
        Assert.Equal(bobPublicExpected, bob.PublicKey);

        Bytes32 aliceShared = X25519.Agreement(alicePrivate, bob.PublicKey);
        Bytes32 bobShared = X25519.Agreement(bobPrivate, alice.PublicKey);

        Assert.Equal(sharedExpected, aliceShared);
        Assert.Equal(sharedExpected, bobShared);
    }

    /// <summary>
    /// RFC 7748 §7: X25519 must reject/zero out low-order points that produce an
    /// all-zero result, rather than returning a predictable shared secret.
    /// </summary>
    [Fact]
    public void Agreement_WithAllZeroPoint_ThrowsInvalidOperationException()
    {
        Bytes32 scalar = new(HexToBytes("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"));
        Bytes32 zeroPoint = new(new byte[32]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => X25519.Agreement(scalar, zeroPoint));

        Assert.Equal("Bad input point: low order point", ex.Message);
    }

    [Fact]
    public void Agreement_WithGeneratedKeyPairs_ProducesSameSharedSecret()
    {
        // 1. Generate two key pairs
        X25519.X25519KeyPair alice = X25519.GenerateKeyPair();
        X25519.X25519KeyPair bob = X25519.GenerateKeyPair();

        // 2. Perform agreement
        Bytes32 aliceShared = X25519.Agreement(alice.PrivateKey, bob.PublicKey);
        Bytes32 bobShared = X25519.Agreement(bob.PrivateKey, alice.PublicKey);

        // 3. Verify
        Assert.False(aliceShared.IsZero);
        Assert.False(bobShared.IsZero);
        Assert.Equal(aliceShared, bobShared);
    }

    [Fact]
    public void GenerateKeyFromPrivateKey_WithGeneratedPrivateKey_ReproducesPublicKey()
    {
        X25519.X25519KeyPair original = X25519.GenerateKeyPair();
        X25519.X25519KeyPair derived = X25519.GenerateKeyFromPrivateKey(original.PrivateKey);

        Assert.Equal(original.PublicKey, derived.PublicKey);
    }

    [Fact]
    public void Agreement_WithRandomPrivateKeys_MatchesBouncyCastle()
    {
        // 1. Generate random private keys
        byte[] alicePriv = GenerateRandom(32);
        byte[] bobPriv = GenerateRandom(32);

        // 2. Clone for clamping (RFC 7748)
        byte[] alicePrivClamped = (byte[])alicePriv.Clone();
        alicePrivClamped[0] &= 248;
        alicePrivClamped[31] &= 127;
        alicePrivClamped[31] |= 64;

        byte[] bobPrivClamped = (byte[])bobPriv.Clone();
        bobPrivClamped[0] &= 248;
        bobPrivClamped[31] &= 127;
        bobPrivClamped[31] |= 64;

        // 3. Nalix Keys
        var aliceNalix = Nalix.Codec.Security.Asymmetric.X25519.GenerateKeyFromPrivateKey(new Bytes32(alicePrivClamped));
        var bobNalix = Nalix.Codec.Security.Asymmetric.X25519.GenerateKeyFromPrivateKey(new Bytes32(bobPrivClamped));

        // 4. Nalix Agreement
        byte[] aliceSecret = Nalix.Codec.Security.Asymmetric.X25519.Agreement(aliceNalix.PrivateKey, bobNalix.PublicKey).ToByteArray();

        // 5. BouncyCastle Keys
        byte[] alicePubBC = new byte[32];
        byte[] bobPubBC = new byte[32];
        BCX25519.ScalarMultBase(alicePrivClamped, 0, alicePubBC, 0);
        BCX25519.ScalarMultBase(bobPrivClamped, 0, bobPubBC, 0);

        // 6. Verify Public Keys Match
        Assert.Equal(alicePubBC, aliceNalix.PublicKey.ToByteArray());
        Assert.Equal(bobPubBC, bobNalix.PublicKey.ToByteArray());

        // 7. BC Agreement
        byte[] bcSecret = new byte[32];
        BCX25519.ScalarMult(alicePrivClamped, 0, bobPubBC, 0, bcSecret, 0);

        Assert.Equal(bcSecret, aliceSecret);
    }
}
