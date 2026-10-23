// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Codec.Security.Hashing;

namespace Nalix.Codec.Tests.Security.Hashing;

public sealed class Pbkdf2Tests
{
    [Fact]
    public void HashVerify_WithSameCredential_ReturnsTrue()
    {
        const string credential = "nalix-secret-123";

        Pbkdf2.Hash(credential, out byte[] salt, out byte[] hash);
        bool ok = Pbkdf2.Verify(credential, salt, hash);

        Assert.Equal(Pbkdf2.SaltSize, salt.Length);
        Assert.Equal(Pbkdf2.KeySize, hash.Length);
        Assert.True(ok);
    }

    [Fact]
    public void Verify_WithWrongCredential_ReturnsFalse()
    {
        const string credential = "correct-password";

        Pbkdf2.Hash(credential, out byte[] salt, out byte[] hash);
        bool ok = Pbkdf2.Verify("wrong-password", salt, hash);

        Assert.False(ok);
    }

    [Fact]
    public void EncodedVerify_AfterEncodedHash_AcceptsOnlyOriginalCredential()
    {
        const string credential = "credential-v2";

        string encoded = Pbkdf2.Encoded.Hash(credential);
        bool ok = Pbkdf2.Encoded.Verify(credential, encoded);
        bool bad = Pbkdf2.Encoded.Verify("bad-credential", encoded);

        Assert.False(string.IsNullOrWhiteSpace(encoded));
        Assert.True(ok);
        Assert.False(bad);
    }

    [Fact]
    public void EncodedVerify_WhenInputIsMalformedOrVersionChanged_ReturnsFalse()
    {
        const string credential = "credential-v2";
        string encoded = Pbkdf2.Encoded.Hash(credential);

        Assert.False(Pbkdf2.Encoded.Verify(credential, "not-base64"));
        Assert.False(Pbkdf2.Encoded.Verify(credential, Convert.ToBase64String([1, 2, 3])));

        byte[] blob = Convert.FromBase64String(encoded);
        blob[0] ^= 0x01; // change version byte
        string changedVersion = Convert.ToBase64String(blob);

        Assert.False(Pbkdf2.Encoded.Verify(credential, changedVersion));
    }

    /// <summary>
    /// A bit-flip anywhere in the stored hash must invalidate verification — guards against
    /// a byte-dropping or truncating comparison bug in the fixed-time equality check used
    /// inside <see cref="Pbkdf2.Verify"/>.
    /// </summary>
    [Fact]
    public void Verify_WithTamperedHash_ReturnsFalse()
    {
        const string credential = "tamper-check-credential";
        Pbkdf2.Hash(credential, out byte[] salt, out byte[] hash);

        hash[^1] ^= 0xFF;

        Assert.False(Pbkdf2.Verify(credential, salt, hash));
    }

    /// <summary>
    /// A different salt for the same credential must produce a different derived hash —
    /// this is the property PBKDF2 relies on to defeat rainbow-table attacks.
    /// </summary>
    [Fact]
    public void Hash_WithSameCredentialTwice_ProducesDifferentHashes()
    {
        const string credential = "same-credential-different-salt";

        Pbkdf2.Hash(credential, out _, out byte[] hash1);
        Pbkdf2.Hash(credential, out _, out byte[] hash2);

        Assert.NotEqual(hash1, hash2);
    }
}
