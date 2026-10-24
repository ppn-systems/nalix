// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Asymmetric;

namespace Nalix.Codec.Tests.Security.Asymmetric;

public sealed class X25519KeyPairTests
{
    [Fact]
    public void Wipe_WhenCalled_ZeroesBothKeys()
    {
        X25519.X25519KeyPair pair = X25519.GenerateKeyPair();

        Assert.False(pair.PrivateKey.IsZero);
        Assert.False(pair.PublicKey.IsZero);

        pair.Wipe();

        Assert.True(pair.PrivateKey.IsZero);
        Assert.True(pair.PublicKey.IsZero);
    }
}
