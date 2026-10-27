// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Runtime.Options;
using Nalix.Runtime.Throttling;
using Xunit;

namespace Nalix.Runtime.Tests.Throttling;

[Collection(TestCollections.ProcessWideServices)]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class TokenBucketLimiterCleanupTests
{
    /// <summary>
    /// A request that already fetched an endpoint state from the shard map, then lost the race against the
    /// stale-entry cleanup, must not mutate that state after it was recycled for a different endpoint.
    /// </summary>
    [Fact]
    public async Task Evaluate_WhenStateIsCleanedUpAfterLookup_DoesNotConsumeTokensOfAnotherEndpoint()
    {
        TokenBucketOptions options = new()
        {
            CapacityTokens = 5,
            InitialTokens = 5,
            RefillTokensPerSecond = 0.001,
            HardLockoutSeconds = 5,
            StaleEntrySeconds = 60,
            CleanupIntervalSeconds = 3600,
            TokenScale = 1_000_000,
            ShardCount = 1,
            SoftViolationWindowSeconds = 5,
            MaxSoftViolations = 5,
            MaxTrackedEndpoints = 1000,
            AdaptiveThrottlingEnabled = false
        };

        using TokenBucketLimiter limiter = new(options);

        // Endpoint A gets its state created.
        _ = limiter.Evaluate(new GateEndpoint("10.0.0.1", gate: null));

        // A second request for A pauses inside the shard-map lookup, right after finding A's state.
        Gate gate = new();
        GateEndpoint probe = new("10.0.0.1", gate);
        gate.Armed = true;
        Task<TokenBucketLimiter.RateLimitDecision> stale = Task.Run(() => limiter.Evaluate(probe), TestContext.Current.CancellationToken);
        Assert.True(gate.Reached.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // The stale-entry cleanup evicts A's state and recycles it.
        MethodInfo cleanup = typeof(TokenBucketLimiter).GetMethod("PERFORM_STALE_CLEANUP", BindingFlags.NonPublic | BindingFlags.Instance)!;
        long future = System.Diagnostics.Stopwatch.GetTimestamp() + (System.Diagnostics.Stopwatch.Frequency * 3600L);
        int removed = (int)cleanup.Invoke(limiter, [future, CancellationToken.None])!;
        Assert.Equal(1, removed);

        // Endpoint B now starts a fresh bucket (on the recycled state object, if the limiter pools it).
        GateEndpoint b = new("10.0.0.2", gate: null);
        Assert.Equal(4, limiter.Evaluate(b).Credit);

        // The paused request resumes.
        gate.Release.Set();
        _ = await stale.WaitAsync(TimeSpan.FromSeconds(5));

        // B must only have paid for its own requests.
        Assert.Equal(3, limiter.Evaluate(b).Credit);
    }

    private sealed class Gate
    {
        public volatile bool Armed;
        public ManualResetEventSlim Reached { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
    }

    private sealed class GateEndpoint(string address, Gate? gate) : INetworkEndpoint, IEquatable<GateEndpoint>
    {
        public Gate? GateRef { get; } = gate;
        public string Address { get; } = address;
        public int Port => 0;
        public bool HasPort => false;
        public bool IsIPv6 => false;

        public bool Equals(GateEndpoint? other)
        {
            if (other is null)
            {
                return false;
            }

            Gate? g = this.GateRef ?? other.GateRef;
            if (g is { Armed: true } && !ReferenceEquals(this, other))
            {
                g.Armed = false;
                g.Reached.Set();
                _ = g.Release.Wait(TimeSpan.FromSeconds(10));
            }

            return string.Equals(this.Address, other.Address, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is GateEndpoint other && this.Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(this.Address);
    }
}
