// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Traversal.Internal;

namespace Nalix.Traversal.Tests.Internal;

/// <summary>
/// Verifies the byte-rate limiter that caps how much data a reflector session may relay.
/// </summary>
public sealed class TokenBucketTests
{
    [Fact]
    public void TryConsume_WhenAmountExceedsCapacity_ReturnsFalse()
    {
        TokenBucket bucket = new(capacity: 512, fillRate: 0);

        Assert.False(bucket.TryConsume(513));
    }

    [Fact]
    public void TryConsume_WhenBucketStartsFull_AllowsCapacityThenRejectsWithoutRefill()
    {
        TokenBucket bucket = new(capacity: 512, fillRate: 0);

        Assert.True(bucket.TryConsume(256));
        Assert.True(bucket.TryConsume(256));
        Assert.False(bucket.TryConsume(1));
    }

    [Fact]
    public void TryConsume_WhenRejected_DoesNotDrainTokens()
    {
        TokenBucket bucket = new(capacity: 100, fillRate: 0);
        Assert.True(bucket.TryConsume(90));

        Assert.False(bucket.TryConsume(20));
        Assert.True(bucket.TryConsume(10));
    }
}
