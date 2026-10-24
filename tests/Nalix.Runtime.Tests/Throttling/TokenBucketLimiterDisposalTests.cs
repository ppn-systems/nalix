// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Nalix.Runtime.Options;
using Nalix.Runtime.Throttling;
using Xunit;

namespace Nalix.Runtime.Tests.Throttling;

public sealed class TokenBucketLimiterDisposalTests
{
    [Fact]
    public void Evaluate_WhenLimiterDisposed_ReturnsHardLockoutDecision()
    {
        TokenBucketOptions options = new()
        {
            CapacityTokens = 2,
            RefillTokensPerSecond = 1,
            CleanupIntervalSeconds = 60,
            StaleEntrySeconds = 60,
            ShardCount = 2,
            MaxTrackedEndpoints = 8
        };

        TokenBucketLimiter limiter = new(options);
        limiter.Dispose();

        TokenBucketLimiter.RateLimitDecision decision = limiter.Evaluate(new TestEndpoint("127.0.0.1"));

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBucketLimiter.RateLimitReason.HardLockout, decision.Reason);
    }

    private sealed record TestEndpoint(string Address, int Port = 0, bool HasPort = false, bool IsIPv6 = false) : INetworkEndpoint
    {
    }
}
