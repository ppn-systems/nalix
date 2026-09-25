// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Asymmetric;
using Xunit;

namespace Nalix.Framework.Tests.Cryptography;

/// <summary>
/// Covers the in-place destruction of key material held in value types, which the handshake relies
/// on to keep secrets out of frames and async state machines once they are no longer needed.
/// </summary>
public sealed class SecretWipeTests
{
    [Fact]
    public void WipeZeroesEveryByteOfTheValue()
    {
        byte[] material = new byte[Bytes32.Size];
        for (int i = 0; i < material.Length; i++)
        {
            material[i] = (byte)(i + 1);
        }

        Bytes32 secret = new(material);
        Assert.False(secret.IsZero);

        Bytes32.Wipe(ref secret);

        Assert.True(secret.IsZero);
        Assert.All(secret.ToByteArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void WipeOnAnAlreadyZeroValueIsANoOp()
    {
        Bytes32 secret = Bytes32.Zero;

        Bytes32.Wipe(ref secret);

        Assert.True(secret.IsZero);
    }

    [Fact]
    public void WipeDoesNotTouchOtherCopiesOfTheValue()
    {
        Bytes32 original = new(new byte[Bytes32.Size].AsSpan());
        Bytes32 secret = Bytes32.Parse("0101010101010101010101010101010101010101010101010101010101010101");
        Bytes32 copy = secret;

        Bytes32.Wipe(ref secret);

        Assert.True(secret.IsZero);
        Assert.False(copy.IsZero);
        Assert.True(original.IsZero);
    }

    [Fact]
    public void KeyPairWipeZeroesBothKeys()
    {
        X25519.X25519KeyPair pair = X25519.GenerateKeyPair();

        Assert.False(pair.PrivateKey.IsZero);
        Assert.False(pair.PublicKey.IsZero);

        pair.Wipe();

        Assert.True(pair.PrivateKey.IsZero);
        Assert.True(pair.PublicKey.IsZero);
    }
}
