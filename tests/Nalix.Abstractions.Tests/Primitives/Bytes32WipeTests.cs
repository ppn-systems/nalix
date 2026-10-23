// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;

namespace Nalix.Abstractions.Tests.Primitives;

/// <summary>
/// Covers the in-place destruction of key material held in <see cref="Nalix.Abstractions.Primitives.Bytes32"/> values.
/// </summary>
public sealed class Bytes32WipeTests
{
    [Fact]
    public void Wipe_WithNonZeroValue_ZeroesEveryByte()
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
    public void Wipe_WhenValueIsAlreadyZero_LeavesValueZero()
    {
        Bytes32 secret = Bytes32.Zero;

        Bytes32.Wipe(ref secret);

        Assert.True(secret.IsZero);
    }

    [Fact]
    public void Wipe_WhenValueWasCopied_LeavesOtherCopiesIntact()
    {
        Bytes32 original = new(new byte[Bytes32.Size].AsSpan());
        Bytes32 secret = Bytes32.Parse("0101010101010101010101010101010101010101010101010101010101010101");
        Bytes32 copy = secret;

        Bytes32.Wipe(ref secret);

        Assert.True(secret.IsZero);
        Assert.False(copy.IsZero);
        Assert.True(original.IsZero);
    }
}
