// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;

namespace Nalix.Runtime.Throttling;

public sealed partial class TokenBucketLimiter
{
    #region Public Types

    /// <summary>
    /// Decision result for a rate-limit check.
    /// </summary>
    public readonly struct RateLimitDecision
    {
        /// <summary>True if request is allowed (token consumed).</summary>
        public bool Allowed { get; init; }

        /// <summary>Milliseconds until at least 1 token becomes available (0 if allowed or no soft backoff).</summary>
        public int RetryAfterMs { get; init; }

        /// <summary>Remaining whole tokens (credit) after the check.</summary>
        public ushort Credit { get; init; }

        /// <summary>Reason for throttling; NONE if allowed.</summary>
        public RateLimitReason Reason { get; init; }
    }

    /// <summary>
    /// Defines a dynamic rate limit policy for evaluation.
    /// </summary>
    public readonly record struct RateLimitPolicy
    {
        /// <summary>Requests per second.</summary>
        public int Rps { get; }

        /// <summary>Capacity in micro-tokens.</summary>
        public long CapacityMicro { get; }

        /// <summary>Tokens to refill per stopwatch tick.</summary>
        public double RefillPerTick { get; }

        /// <summary>Tokens to refill per second in micro-tokens.</summary>
        public long RefillPerSecMicro { get; }

        /// <summary>The scale factor for tokens.</summary>
        public int TokenScale { get; }

        /// <summary>Maximum soft violations before hard lockout.</summary>
        public int MaxSoftViolations { get; }

        /// <summary>Hard lockout duration in seconds after exceeding soft violation threshold.</summary>
        public int HardLockoutSeconds { get; }

        /// <summary>
        /// Initializes a new instance of the dynamic policy.
        /// </summary>
        public RateLimitPolicy(int rps, double burst, int tokenScale, double swFreq, int hardLockoutSec, int maxSoftViolations)
        {
            this.Rps = rps;
            this.TokenScale = tokenScale;
            this.CapacityMicro = (long)(burst * tokenScale);
            this.RefillPerTick = rps * tokenScale / swFreq;
            this.HardLockoutSeconds = hardLockoutSec;
            this.MaxSoftViolations = maxSoftViolations;
            this.RefillPerSecMicro = (long)Math.Round(rps * (double)tokenScale);
        }
    }

    /// <summary>
    /// Throttling reason taxonomy.
    /// </summary>
    public enum RateLimitReason : byte
    {
        /// <summary>NONE. </summary>
        None = 0,

        /// <summary>
        /// The request was denied due to a soft throttle, typically when the rate limit is exceeded but not enough to trigger a hard lockout.
        /// </summary>
        SoftThrottle = 1,

        /// <summary>
        /// The request was denied due to a hard lockout, typically after repeated violations or exceeding a critical threshold.
        /// </summary>
        HardLockout = 2
    }

    #endregion Public Types

    #region Private Types

    internal sealed class EndpointState : IPoolable
    {
        public long LastSeenSw;
        public long MicroBalance;
        public int SoftViolations;
        public long LastViolationSw;
        public long AccumulatedMicro;
        public long LastRefillSwTicks;
        public long HardBlockedUntilSw;
        public int Generation;

        /// <summary>
        /// Set (under <see cref="Lock"/>) once the state was removed from its shard map. A retired state is never
        /// pooled or reused, so a request that fetched it just before removal can detect that and look it up again.
        /// </summary>
        public int Retired;

        public readonly Lock Lock = new();

        public void ResetForPool()
        {
            Volatile.Write(ref LastSeenSw, 0);
            Volatile.Write(ref MicroBalance, 0);
            Volatile.Write(ref SoftViolations, 0);
            Volatile.Write(ref LastViolationSw, 0);
            Volatile.Write(ref AccumulatedMicro, 0);
            Volatile.Write(ref LastRefillSwTicks, 0);
            Volatile.Write(ref HardBlockedUntilSw, 0);
            _ = Interlocked.Increment(ref Generation);
        }
    }

    /// <summary>A shard contains a dictionary of endpoint states.</summary>
    private sealed class Shard
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<INetworkEndpoint, EndpointState> Map = new();
    }

    #endregion Private Types
}
