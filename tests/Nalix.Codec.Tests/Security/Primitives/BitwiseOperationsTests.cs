// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Text;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Primitives;

namespace Nalix.Codec.Tests.Security.Primitives;

public sealed class BitwiseOperationsTests
{
    [Fact]
    public void FixedTimeEquals_WithEqualDifferentAndShorterInput_ReturnsExpectedValues()
    {
        ReadOnlySpan<byte> a = [1, 2, 3];
        ReadOnlySpan<byte> b = [1, 2, 3];
        ReadOnlySpan<byte> c = [1, 2, 4];
        ReadOnlySpan<byte> d = [1, 2];

        Assert.True(BitwiseOperations.FixedTimeEquals(a, b));
        Assert.False(BitwiseOperations.FixedTimeEquals(a, c));
        Assert.False(BitwiseOperations.FixedTimeEquals(a, d));
    }

    [Fact]
    public void IsZero_WithEmptyZeroAndNonZeroInput_ReturnsExpectedValues()
    {
        Assert.True(BitwiseOperations.IsZero(ReadOnlySpan<byte>.Empty));
        Assert.True(BitwiseOperations.IsZero([0, 0, 0]));
        Assert.False(BitwiseOperations.IsZero([0, 1, 0]));
    }
}
