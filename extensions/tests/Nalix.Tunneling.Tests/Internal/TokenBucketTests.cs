// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Tunneling.Internal;

namespace Nalix.Tunneling.Tests.Internal;

/// <summary>
/// Verifies the byte-rate limiter that caps a tunnel's throughput.
/// </summary>
public sealed class TokenBucketTests
{
    [Fact]
    public void TryConsume_WhenAmountExceedsCapacity_ReturnsFalse()
    {
        TokenBucket bucket = new(capacity: 100, fillRate: 0);

        Assert.False(bucket.TryConsume(101));
    }

    [Fact]
    public void TryConsume_WhenBucketStartsFull_AllowsCapacityThenRejectsWithoutRefill()
    {
        TokenBucket bucket = new(capacity: 100, fillRate: 0);

        Assert.True(bucket.TryConsume(60));
        Assert.True(bucket.TryConsume(40));
        Assert.False(bucket.TryConsume(1));
    }

    [Fact]
    public async Task ConsumeOrWaitAsync_WhenAmountExceedsCapacity_ClampsToCapacityAndCompletes()
    {
        TokenBucket bucket = new(capacity: 50, fillRate: 1_000_000);

        await bucket.ConsumeOrWaitAsync(500).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConsumeOrWaitAsync_WhenBucketIsEmpty_WaitsForRefillBeforeCompleting()
    {
        // 1000 bytes/s: draining the bucket and asking for 50 more bytes must take roughly 50 ms.
        TokenBucket bucket = new(capacity: 100, fillRate: 1000);
        Assert.True(bucket.TryConsume(100));

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        await bucket.ConsumeOrWaitAsync(50).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds >= 30, $"Expected a refill wait, took {watch.ElapsedMilliseconds} ms.");
    }
}
