// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Runtime.Options;
using Nalix.Runtime.Throttling;
using Xunit;

namespace Nalix.Runtime.Tests.Throttling;

[Collection(TestCollections.ProcessWideServices)]
public sealed class TokenBucketLimiterTests
{
    [Fact]
    public void Evaluate_WithNullEndpoint_ThrowsArgumentNullException()
    {
        using var limiter = new TokenBucketLimiter(CreateOptions());
        Action act = () => limiter.Evaluate(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("key");
    }

    [Fact]
    public void Evaluate_WithEmptyAddress_ThrowsArgumentException()
    {
        using var limiter = new TokenBucketLimiter(CreateOptions());
        Action act = () => limiter.Evaluate(new TestEndpoint(""));
        act.Should().Throw<ArgumentException>().WithMessage("*address*");
    }

    [Fact]
    public void Evaluate_WithinBurstCapacity_AllowsRequestsThenDenies()
    {
        var options = CreateOptions();
        options.CapacityTokens = 5;
        options.InitialTokens = 5;
        options.RefillTokensPerSecond = 0.001; // Minimal refill to pass validation while staying stable
        using var limiter = new TokenBucketLimiter(options);
        var endpoint = new TestEndpoint("1.1.1.1");

        for (int i = 0; i < 5; i++)
        {
            var decision = limiter.Evaluate(endpoint);
            decision.Allowed.Should().BeTrue();
            decision.Credit.Should().Be((ushort)(4 - i));
        }

        limiter.Evaluate(endpoint).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_AfterRefillTime_AllowsNewRequests()
    {
        var options = CreateOptions();
        options.CapacityTokens = 1;
        options.InitialTokens = 1;
        options.RefillTokensPerSecond = 5; // Refill 1 token every 200ms
        options.TokenScale = 1000;
        
        using var limiter = new TokenBucketLimiter(options);
        var endpoint = new TestEndpoint("2.2.2.2");

        // 1. Consume initial
        limiter.Evaluate(endpoint).Allowed.Should().BeTrue();
        
        // 2. Immediate next should be blocked (200ms window)
        limiter.Evaluate(endpoint).Allowed.Should().BeFalse();

        // 3. Wait for refill (200ms + buffer)
        await Task.Delay(400);

        limiter.Evaluate(endpoint).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WithRepeatedViolations_EscalatesToHardLockout()
    {
        var options = CreateOptions();
        options.CapacityTokens = 1;
        options.InitialTokens = 1;
        options.MaxSoftViolations = 2;
        options.SoftViolationWindowSeconds = 10;
        options.HardLockoutSeconds = 60;
        options.RefillTokensPerSecond = 0.001; // Extremely slow refill to prevent flakiness
        
        using var limiter = new TokenBucketLimiter(options);
        var endpoint = new TestEndpoint("3.3.3.3");

        // 1. Consume
        limiter.Evaluate(endpoint).Allowed.Should().BeTrue();

        // 2. First violation (soft)
        var soft1 = limiter.Evaluate(endpoint);
        soft1.Allowed.Should().BeFalse();
        soft1.Reason.Should().Be(TokenBucketLimiter.RateLimitReason.SoftThrottle);

        // 3. Second violation (triggers hard lockout)
        var hard = limiter.Evaluate(endpoint);
        hard.Allowed.Should().BeFalse();
        hard.Reason.Should().Be(TokenBucketLimiter.RateLimitReason.HardLockout);
        hard.RetryAfterMs.Should().BeInRange(59000, 61000);
    }

    [Fact]
    public void Evaluate_WithDynamicPolicy_UsesProvidedPolicy()
    {
        using var limiter = new TokenBucketLimiter(CreateOptions());
        var endpoint = new TestEndpoint("4.4.4.4");

        // Create a strict policy (1 token capacity, 0 refill)
        var policy = new TokenBucketLimiter.RateLimitPolicy(
            rps: 0, // Disable refill 
            burst: 1.0, 
            tokenScale: 1000, 
            swFreq: System.Diagnostics.Stopwatch.Frequency, 
            hardLockoutSec: 10, 
            maxSoftViolations: 1);

        limiter.Evaluate(endpoint, policy).Allowed.Should().BeTrue();
        
        var second = limiter.Evaluate(endpoint, policy);
        second.Allowed.Should().BeFalse();
        second.Reason.Should().Be(TokenBucketLimiter.RateLimitReason.HardLockout);
    }

    [Fact]
    public void Evaluate_WhenMaxTrackedEndpointsReached_RejectsNewEndpoints()
    {
        var options = CreateOptions();
        options.MaxTrackedEndpoints = 2;
        
        using var limiter = new TokenBucketLimiter(options);
        
        limiter.Evaluate(new TestEndpoint("ip1")).Allowed.Should().BeTrue();
        limiter.Evaluate(new TestEndpoint("ip2")).Allowed.Should().BeTrue();
        
        var third = limiter.Evaluate(new TestEndpoint("ip3"));
        third.Allowed.Should().BeFalse();
        third.Reason.Should().Be(TokenBucketLimiter.RateLimitReason.HardLockout);
    }

    [Fact]
    public void GenerateReport_AfterEvaluate_ReturnsFormattedString()
    {
        using var limiter = new TokenBucketLimiter(CreateOptions());
        limiter.Evaluate(new TestEndpoint("report-ip"));

        string report = limiter.GenerateReport();
        report.Should().Contain("TokenBucketLimiter Status");
        report.Should().Contain("TrackedEndpoints    : 1");
    }

    [Fact]
    public void Evaluate_AfterDispose_ReturnsHardLockout()
    {
        var limiter = new TokenBucketLimiter(CreateOptions());
        limiter.Dispose();

        var decision = limiter.Evaluate(new TestEndpoint("any"));
        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(TokenBucketLimiter.RateLimitReason.HardLockout);
    }

    /// <summary>
    /// Area 3 (rate limiting concurrency exactness): capacity=50 tokens, 200 threads racing to
    /// call <see cref="TokenBucketLimiter.Evaluate(INetworkEndpoint, TokenBucketLimiter.RateLimitPolicy?)"/>
    /// on the SAME endpoint concurrently with refill disabled must allow EXACTLY 50 requests total
    /// — never more (would indicate a token-accounting race allowing over-admission) and never
    /// fewer (would indicate lost concurrent decrements). Repeated 200 times with a fixed seed to
    /// catch rare interleavings; run count only, seeded shuffling of thread start order via Random.
    /// </summary>
    [Fact]
    [Trait("Category", "Stress")]
    public void Evaluate_WithConcurrentRaceOnSameEndpoint_AllowsExactlyCapacityCount()
    {
        const int seed = 20260704;
        const int capacity = 50;
        const int threadCount = 64; // ponytail: dedicated Threads avoid ThreadPool injection throttling under Barrier blocking; 200 Task.Run made this test take ~3min, ThreadPool has no such issue but keep count modest for CI time

        var options = CreateOptions();
        options.CapacityTokens = capacity;
        options.InitialTokens = capacity;
        options.RefillTokensPerSecond = 0.001; // minimal refill (validation floor), negligible during the race window

        using var limiter = new TokenBucketLimiter(options);
        var endpoint = new TestEndpoint("concurrent-race-ip");

        System.Random rng = new(seed);
        int allowedCount = 0;
        using System.Threading.Barrier barrier = new(threadCount);

        Thread[] threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int delayTicks = rng.Next(0, 5);
            threads[i] = new Thread(() =>
            {
                for (int spin = 0; spin < delayTicks; spin++)
                {
                    Thread.SpinWait(1);
                }
                barrier.SignalAndWait();
                var decision = limiter.Evaluate(endpoint);
                if (decision.Allowed)
                {
                    Interlocked.Increment(ref allowedCount);
                }
            });
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        allowedCount.Should().Be(capacity, $"seed={seed}: exactly {capacity} of {threadCount} concurrent requests racing on the same endpoint must be admitted, never more (over-admission) or fewer (lost decrements)");
    }

    private static TokenBucketOptions CreateOptions()
    {
        return new TokenBucketOptions
        {
            CapacityTokens = 10,
            RefillTokensPerSecond = 10,
            HardLockoutSeconds = 5,
            StaleEntrySeconds = 60,
            CleanupIntervalSeconds = 60,
            TokenScale = 1000000,
            ShardCount = 8,
            SoftViolationWindowSeconds = 5,
            MaxSoftViolations = 5,
            MaxTrackedEndpoints = 1000,
            InitialTokens = -1, // Full bucket
            AdaptiveThrottlingEnabled = false
        };
    }

    private sealed record TestEndpoint(string Address) : INetworkEndpoint
    {
        public int Port => 0;
        public bool HasPort => false;
        public bool IsIPv6 => false;
    }

    [Fact]
    public void Evaluate_WithAdaptiveThrottling_ScalesRetryAfter()
    {
        var stub = new StubTaskManager { ConcurrencyLimitRatio = 0.5 };
        Nalix.Framework.Injection.InstanceManager.Instance.Register<Nalix.Abstractions.Concurrency.ITaskManager>(stub);

        try
        {
            var options = CreateOptions();
            options.CapacityTokens = 10;
            options.InitialTokens = 0; // Cold-start/empty
            options.RefillTokensPerSecond = 10;
            options.AdaptiveThrottlingEnabled = true;

            using var limiter = new TokenBucketLimiter(options);
            var endpoint = new TestEndpoint("adaptive-ip");

            var decision = limiter.Evaluate(endpoint);
            decision.Allowed.Should().BeFalse();
            // With scale = 0.5, retry after should be doubled: 200ms instead of 100ms
            decision.RetryAfterMs.Should().Be(200);
        }
        finally
        {
            Nalix.Framework.Injection.InstanceManager.Instance.RemoveInstance(typeof(Nalix.Abstractions.Concurrency.ITaskManager));
        }
    }

    [Fact]
    public void Evaluate_WithAdaptiveThrottlingDisabled_DoesNotScaleRetryAfter()
    {
        var stub = new StubTaskManager { ConcurrencyLimitRatio = 0.5 };
        Nalix.Framework.Injection.InstanceManager.Instance.Register<Nalix.Abstractions.Concurrency.ITaskManager>(stub);

        try
        {
            var options = CreateOptions();
            options.CapacityTokens = 10;
            options.InitialTokens = 0; // Cold-start/empty
            options.RefillTokensPerSecond = 10;
            options.AdaptiveThrottlingEnabled = false; // Disabled!

            using var limiter = new TokenBucketLimiter(options);
            var endpoint = new TestEndpoint("adaptive-disabled-ip");

            var decision = limiter.Evaluate(endpoint);
            decision.Allowed.Should().BeFalse();
            // With adaptive throttling disabled, retry after should stay 100ms despite ratio being 0.5
            decision.RetryAfterMs.Should().Be(100);
        }
        finally
        {
            Nalix.Framework.Injection.InstanceManager.Instance.RemoveInstance(typeof(Nalix.Abstractions.Concurrency.ITaskManager));
        }
    }

    private sealed class StubTaskManager : Nalix.Abstractions.Concurrency.ITaskManager
    {
        public double ConcurrencyLimitRatio { get; set; } = 1.0;
        public string Title => "";
        public string Report => "";

        public Nalix.Abstractions.Concurrency.IRecurringHandle ScheduleRecurring(string name, TimeSpan interval, Func<System.Threading.CancellationToken, ValueTask> work, Nalix.Abstractions.Concurrency.IRecurringOptions? options = null) => null!;
        public ValueTask RunOnceAsync(string name, Func<System.Threading.CancellationToken, ValueTask> work, System.Threading.CancellationToken ct = default) => default;
        public Nalix.Abstractions.Concurrency.IWorkerHandle ScheduleWorker(Nalix.Abstractions.Concurrency.IWorker worker) => null!;
        public Nalix.Abstractions.Concurrency.IWorkerHandle ScheduleWorker(string name, string group, Func<Nalix.Abstractions.Concurrency.IWorkerContext, System.Threading.CancellationToken, ValueTask> work, Nalix.Abstractions.Concurrency.IWorkerOptions? options = null) => null!;
        public int CancelAllWorkers() => 0;
        public void CancelWorker(Nalix.Abstractions.Identity.ISnowflake id) {}
        public int CancelGroup(string group) => 0;
        public void CancelRecurring(string name) {}
        public System.Collections.Generic.IReadOnlyCollection<Nalix.Abstractions.Concurrency.IWorkerHandle> GetWorkers(bool runningOnly = true, string? group = null) => Array.Empty<Nalix.Abstractions.Concurrency.IWorkerHandle>();
        public bool TryGetWorker(Nalix.Abstractions.Identity.ISnowflake id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Nalix.Abstractions.Concurrency.IWorkerHandle? handle) { handle = null; return false; }
        public System.Collections.Generic.IReadOnlyCollection<Nalix.Abstractions.Concurrency.IRecurringHandle> GetRecurring() => Array.Empty<Nalix.Abstractions.Concurrency.IRecurringHandle>();
        public bool TryGetRecurring(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Nalix.Abstractions.Concurrency.IRecurringHandle? handle) { handle = null; return false; }
        public Task WaitGroupAsync(string group, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public void ScheduleWorker<TState>(string name, Action<TState> work, TState state) {}
        public string GenerateReport() => "";
        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer) {}
    }
}
