// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Environment.Hashing;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Options;

namespace Nalix.Runtime.Throttling;

/// <summary>
/// High-performance token-bucket based rate limiter with per-endpoint state,
/// using Stopwatch ticks for time arithmetic and fixed-point token precision.
/// Provides precise Retry-After and Credit for client backoff and flow control.
/// </summary>
[Injectable]
[SkipLocalsInit]
[DebuggerNonUserCode]
public sealed partial class TokenBucketLimiter : IDisposable, IAsyncDisposable, IReportable
{
    #region Constants

    private const int CancellationCheckFrequency = 256;
    private const double MaxDelayMs = int.MaxValue - 1000.0;

    #endregion Constants

    #region Fields

    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    private readonly Shard[] _shards;
    private readonly double _swFreq;
    private readonly long _capacityMicro;
    private readonly double _refillPerTick;
    private readonly long _refillPerSecMicro;
    private readonly int _cleanupIntervalSec;
    private readonly long _initialBalanceMicro;
    private readonly TokenBucketOptions _options;
    private readonly ITaskManager? _taskManager;
    private readonly bool _adaptiveThrottlingEnabled;

    private int _totalEndpointCount;
    private int _cleanupShardStart;
    private volatile bool _disposed;
    private IRecurringHandle? _cleanupJob;
    private long _totalCleaned;
    private int _peakTrackedEndpoints;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Recurring job name for cleanup task. Format: "token.bucket".
    /// </summary>
    public static readonly string RecurringName;

    #endregion Properties

    #region Constructors

    static TokenBucketLimiter() => RecurringName = "token.bucket";

    /// <summary>
    /// Creates a new TokenBucketLimiter with provided options.
    /// </summary>
    /// <param name="options">Configuration options for the limiter.</param>
    /// <exception cref="InternalErrorException">Thrown when options validation fails.</exception>
    public TokenBucketLimiter(TokenBucketOptions? options = null)
    {
        _options = options ?? ConfigurationManager.Instance.Get<TokenBucketOptions>();
        _options.Validate();

        _totalEndpointCount = 0;
        _shards = new Shard[_options.ShardCount];
        _swFreq = Stopwatch.Frequency;
        _cleanupIntervalSec = _options.CleanupIntervalSeconds;
        _capacityMicro = (long)_options.CapacityTokens * _options.TokenScale;
        _refillPerTick = _options.RefillTokensPerSecond * _options.TokenScale / _swFreq;
        _refillPerSecMicro = (long)Math.Round(_options.RefillTokensPerSecond * _options.TokenScale);

        _initialBalanceMicro = this.CalculateInitialBalance();

        for (int i = 0; i < _shards.Length; i++)
        {
            _shards[i] = new Shard();
        }

        _taskManager = InstanceManager.Instance.GetExistingInstance<ITaskManager>();
        _adaptiveThrottlingEnabled = _options.AdaptiveThrottlingEnabled && _taskManager is not null;

        int poolCapacity = Math.Clamp(_options.MaxTrackedEndpoints / 2, 1024, 65536);
        _ = s_pool.SetMaxCapacity<EndpointState>(poolCapacity);
        _ = s_pool.Prealloc<EndpointState>(Math.Min(64, _options.MaxTrackedEndpoints));

        this.SCHEDULE_CLEANUP_JOB();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double GetAdaptiveScale()
    {
        if (!_adaptiveThrottlingEnabled)
        {
            return 1.0;
        }

        return _taskManager!.ConcurrencyLimitRatio;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenBucketLimiter"/> class with default options.
    /// </summary>
    public TokenBucketLimiter() : this(null) { }

    #endregion Constructors

    #region Public API

    /// <summary>
    /// Checks and consumes 1 token for the given endpoint.  Returns decision with RetryAfter and Credit.
    /// </summary>
    /// <param name="key">The network endpoint to check.</param>
    /// <returns>A decision indicating whether the request is allowed.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the limiter has been disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown when key is null.</exception>
    /// <exception cref="ArgumentException">Thrown when key address is null or empty.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public RateLimitDecision Evaluate(INetworkEndpoint key)
    {
        /*
         * [Decision Workflow]
         * 1. Validate endpoint.
         * 2. Direct path for ScopedEndpoint cache check (bypasses global dictionary lookups).
         * 3. Select shard based on endpoint hash.
         * 4. Retrieve or create endpoint state.
         * 5. Perform high-precision token bucket evaluation.
         */
        if (_disposed)
        {
            return new RateLimitDecision
            {
                Allowed = false,
                RetryAfterMs = 0,
                Credit = 0,
                Reason = RateLimitReason.HardLockout
            };
        }

        VALIDATE_ENDPOINT(key);

        long now = Stopwatch.GetTimestamp();

        if (key is PolicyRateLimiter.ScopedEndpoint scopedKey)
        {
            EndpointState? cached = scopedKey.CachedState;
            if (cached != null && cached.Generation == scopedKey.CachedGeneration && Volatile.Read(ref cached.LastSeenSw) != 0)
            {
                if (this.TRY_EVALUATE_RATE_LIMIT(cached, now, out RateLimitDecision cachedDecision))
                {
                    return cachedDecision;
                }
            }
        }

        Shard shard = this.SELECT_SHARD(key);

        while (true)
        {
            EndpointState? state = this.GET_OR_CREATE_ENDPOINT_STATE(key, shard, now, out bool limitReached);
            if (limitReached || state == null)
            {
                return this.CREATE_LIMIT_REACHED_DECISION();
            }

            if (key is PolicyRateLimiter.ScopedEndpoint newScopedKey)
            {
                newScopedKey.CachedState = state;
                newScopedKey.CachedGeneration = state.Generation;
            }

            // A state retired by the cleanup between the lookup and the lock is looked up again.
            if (this.TRY_EVALUATE_RATE_LIMIT(state, now, out RateLimitDecision decision))
            {
                return decision;
            }
        }
    }

    /// <summary>
    /// Checks and consumes 1 token for the given endpoint using a dynamically provided policy.
    /// </summary>
    /// <param name="key">The network endpoint to check.</param>
    /// <param name="policy">The dynamic rate limit policy to apply.</param>
    /// <returns>A decision indicating whether the request is allowed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public RateLimitDecision Evaluate(INetworkEndpoint key, in RateLimitPolicy policy)
    {
        if (_disposed)
        {
            return new RateLimitDecision
            {
                Allowed = false,
                RetryAfterMs = 0,
                Credit = 0,
                Reason = RateLimitReason.HardLockout
            };
        }

        VALIDATE_ENDPOINT(key);

        long now = Stopwatch.GetTimestamp();

        if (key is PolicyRateLimiter.ScopedEndpoint scopedKey)
        {
            EndpointState? cached = scopedKey.CachedState;
            if (cached != null && cached.Generation == scopedKey.CachedGeneration && Volatile.Read(ref cached.LastSeenSw) != 0)
            {
                if (this.TRY_EVALUATE_RATE_LIMIT(cached, now, in policy, out RateLimitDecision cachedDecision))
                {
                    return cachedDecision;
                }
            }
        }

        Shard shard = this.SELECT_SHARD(key);

        while (true)
        {
            EndpointState? state = this.GET_OR_CREATE_ENDPOINT_STATE(key, shard, now, out bool limitReached, in policy);
            if (limitReached || state == null)
            {
                return this.CREATE_LIMIT_REACHED_DECISION();
            }

            if (key is PolicyRateLimiter.ScopedEndpoint newScopedKey)
            {
                newScopedKey.CachedState = state;
                newScopedKey.CachedGeneration = state.Generation;
            }

            // A state retired by the cleanup between the lookup and the lock is looked up again.
            if (this.TRY_EVALUATE_RATE_LIMIT(state, now, in policy, out RateLimitDecision decision))
            {
                return decision;
            }
        }
    }

    #endregion Public API

    #region Endpoint State Management

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VALIDATE_ENDPOINT(INetworkEndpoint key)
    {
        ArgumentNullException.ThrowIfNull(key);

        Span<char> buffer = stackalloc char[64];
        if (key.TryFormatAddress(buffer, out int written))
        {
            if (written == 0)
            {
                THROW_KEY_ADDRESS_EMPTY();
            }
        }
        else
        {
#pragma warning disable NALIX072 // Validation check; one-time per endpoint, not a hot-path allocation.
            if (string.IsNullOrEmpty(key.Address))
            {
                THROW_KEY_ADDRESS_EMPTY();
            }
#pragma warning restore NALIX072
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EndpointState? GET_OR_CREATE_ENDPOINT_STATE(INetworkEndpoint key, Shard shard, long now, out bool limitReached)
    {
        limitReached = false;
        // Fast-path: endpoint already tracked
        if (shard.Map.TryGetValue(key, out EndpointState? existingState))
        {
            return existingState;
        }

        // Slow-path: create new state with limit check
        return this.CREATE_NEW_ENDPOINT_STATE(key, shard, now, out limitReached);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EndpointState? GET_OR_CREATE_ENDPOINT_STATE(INetworkEndpoint key, Shard shard, long now, out bool limitReached, in RateLimitPolicy policy)
    {
        limitReached = false;
        if (shard.Map.TryGetValue(key, out EndpointState? existingState))
        {
            return existingState;
        }

        return this.CREATE_NEW_ENDPOINT_STATE(key, shard, now, out limitReached, in policy);
    }

    private EndpointState? CREATE_NEW_ENDPOINT_STATE(INetworkEndpoint key, Shard shard, long now, out bool limitReached)
    {
        limitReached = false;
        // Pre-check limit before allocation
        if (this.IS_ENDPOINT_LIMIT_REACHED())
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog(
                        "RT.TokenBucketLimiter:Internal",
                        $"endpoint-limit-reached-precheck count={_totalEndpointCount} limit={_options.MaxTrackedEndpoints}"));
            }

            limitReached = true;
            return null;
        }

        EndpointState newState = this.CREATE_INITIAL_ENDPOINT_STATE(now);
        int newCount = Interlocked.Increment(ref _totalEndpointCount);

        if (!shard.Map.TryAdd(key, newState))
        {
            // Lost the race - another thread added it first
            _ = Interlocked.Decrement(ref _totalEndpointCount);

            return shard.Map[key];
        }

        // Successfully added - double-check limit
        if (this.SHOULD_REJECT_DUE_TO_LIMIT(newCount))
        {
            this.REMOVE_NEWLY_ADDED_ENDPOINT(key, shard);
            limitReached = true;
            return null;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog(
                    "RT.TokenBucketLimiter:Internal",
                    $"new-endpoint total={_totalEndpointCount}"));
        }

        return newState;
    }

    private EndpointState? CREATE_NEW_ENDPOINT_STATE(INetworkEndpoint key, Shard shard, long now, out bool limitReached, in RateLimitPolicy policy)
    {
        limitReached = false;
        if (this.IS_ENDPOINT_LIMIT_REACHED())
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog(
                        "RT.TokenBucketLimiter:Internal",
                        $"endpoint-limit-reached-precheck count={_totalEndpointCount} limit={_options.MaxTrackedEndpoints}"));
            }
            limitReached = true;
            return null;
        }

        EndpointState newState = this.CREATE_INITIAL_ENDPOINT_STATE(now, in policy);
        int newCount = Interlocked.Increment(ref _totalEndpointCount);

        if (!shard.Map.TryAdd(key, newState))
        {
            _ = Interlocked.Decrement(ref _totalEndpointCount);
            return shard.Map[key];
        }

        if (this.SHOULD_REJECT_DUE_TO_LIMIT(newCount))
        {
            this.REMOVE_NEWLY_ADDED_ENDPOINT(key, shard);
            limitReached = true;
            return null;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog(
                    "RT.TokenBucketLimiter:Internal",
                    $"new-endpoint total={_totalEndpointCount}"));
        }

        return newState;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EndpointState CREATE_INITIAL_ENDPOINT_STATE(long now)
    {
        EndpointState state = s_pool.Get<EndpointState>();
        state.LastSeenSw = now;
        state.SoftViolations = 0;
        state.LastViolationSw = 0;
        state.HardBlockedUntilSw = 0;
        state.LastRefillSwTicks = now;
        state.MicroBalance = _initialBalanceMicro; // Start with full bucket
        return state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EndpointState CREATE_INITIAL_ENDPOINT_STATE(long now, in RateLimitPolicy policy)
    {
        EndpointState state = s_pool.Get<EndpointState>();
        state.LastSeenSw = now;
        state.SoftViolations = 0;
        state.LastViolationSw = 0;
        state.HardBlockedUntilSw = 0;
        state.LastRefillSwTicks = now;
        state.MicroBalance = this.CalculateInitialBalance(in policy);
        return state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IS_ENDPOINT_LIMIT_REACHED()
    {
        if (_options.MaxTrackedEndpoints <= 0)
        {
            return false;
        }

        int currentCount = Volatile.Read(ref _totalEndpointCount);
        return currentCount >= _options.MaxTrackedEndpoints;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SHOULD_REJECT_DUE_TO_LIMIT(int newCount) => _options.MaxTrackedEndpoints > 0 && newCount > _options.MaxTrackedEndpoints;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void REMOVE_NEWLY_ADDED_ENDPOINT(INetworkEndpoint key, Shard shard)
    {
        // Another request may already have fetched this state from the map, so retire it (under its lock)
        // instead of pooling it; see TRY_RETIRE_ENDPOINT.
        if (shard.Map.TryGetValue(key, out EndpointState? state))
        {
            lock (state.Lock)
            {
                _ = this.TRY_RETIRE_ENDPOINT(new KeyValuePair<INetworkEndpoint, EndpointState>(key, state), shard);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RateLimitDecision CREATE_LIMIT_REACHED_DECISION()
    {
        return new RateLimitDecision
        {
            Allowed = false,
            RetryAfterMs = _options.HardLockoutSeconds * 1000,
            Credit = 0,
            Reason = RateLimitReason.HardLockout
        };
    }

    #endregion Endpoint State Management

    #region Rate Limit Evaluation

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TRY_EVALUATE_RATE_LIMIT(EndpointState state, long now, out RateLimitDecision decision)
    {
        lock (state.Lock)
        {
            // The cleanup removes (and retires) a state under this same lock, so once we hold it a
            // non-retired state is guaranteed to still belong to its endpoint.
            if (Volatile.Read(ref state.Retired) != 0)
            {
                decision = default;
                return false;
            }

            /*
             * HIGH-PRECISION TOKEN BUCKET (NANOSECOND RESOLUTION):
             * Instead of storing whole tokens, we store 'MicroTokens' (MicroBalance).
             * One whole token = TokenScale (e.g., 1,000,000).
             * * This allows us to track fractions of tokens without floating point math,
             * and provides extremely smooth throttling even at very high rates (100k+ req/sec).
             */
            state.LastSeenSw = now;

            // Step 1: Check hard lockout first (set when users repeatedly exceed soft limits)
            if (this.IS_HARD_BLOCKED(state, now, out RateLimitDecision blockedDecision))
            {
                decision = blockedDecision;
                return true;
            }

            // Step 2: Refill tokens based on nanoseconds elapsed since last packet from this IP
            this.REFILL_TOKENS(now, state);

            // Step 3: Check if we have at least one whole token (TokenScale units)
            if (this.CAN_CONSUME_TOKEN(state))
            {
                decision = this.CONSUME_TOKEN_AND_CREATE_DECISION(state);
                return true;
            }

            // Step 4: No tokens left - apply penalty or block
            decision = this.HANDLE_INSUFFICIENT_TOKENS(state, now);
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TRY_EVALUATE_RATE_LIMIT(EndpointState state, long now, in RateLimitPolicy policy, out RateLimitDecision decision)
    {
        lock (state.Lock)
        {
            if (Volatile.Read(ref state.Retired) != 0)
            {
                decision = default;
                return false;
            }

            state.LastSeenSw = now;

            if (this.IS_HARD_BLOCKED(state, now, out RateLimitDecision blockedDecision, in policy))
            {
                decision = blockedDecision;
                return true;
            }

            this.REFILL_TOKENS(now, state, in policy);

            if (this.CAN_CONSUME_TOKEN(state, in policy))
            {
                decision = this.CONSUME_TOKEN_AND_CREATE_DECISION(state, in policy);
                return true;
            }

            decision = this.HANDLE_INSUFFICIENT_TOKENS(state, now, in policy);
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IS_HARD_BLOCKED(EndpointState state, long now, out RateLimitDecision decision)
    {
        if (state.HardBlockedUntilSw > now)
        {
            int retryMs = this.CALCULATE_DELAY_MS(now, state.HardBlockedUntilSw);
            this.LOG_HARD_BLOCKED(retryMs);

            decision = new RateLimitDecision
            {
                Allowed = false,
                RetryAfterMs = retryMs,
                Credit = CALCULATE_REMAINING_CREDIT(state.MicroBalance, _options.TokenScale),
                Reason = RateLimitReason.HardLockout
            };
            return true;
        }

        decision = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IS_HARD_BLOCKED(EndpointState state, long now, out RateLimitDecision decision, in RateLimitPolicy policy)
    {
        if (state.HardBlockedUntilSw > now)
        {
            int retryMs = this.CALCULATE_DELAY_MS(now, state.HardBlockedUntilSw);
            decision = new RateLimitDecision
            {
                Allowed = false,
                RetryAfterMs = retryMs,
                Credit = CALCULATE_REMAINING_CREDIT(state.MicroBalance, policy.TokenScale),
                Reason = RateLimitReason.HardLockout
            };
            return true;
        }

        decision = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CAN_CONSUME_TOKEN(EndpointState state) => state.MicroBalance >= _options.TokenScale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CAN_CONSUME_TOKEN(EndpointState state, in RateLimitPolicy policy) => state.MicroBalance >= policy.TokenScale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RateLimitDecision CONSUME_TOKEN_AND_CREATE_DECISION(EndpointState state)
    {
        state.SoftViolations = 0;
        state.MicroBalance -= _options.TokenScale;

        ushort credit = CALCULATE_REMAINING_CREDIT(state.MicroBalance, _options.TokenScale);

        if (credit <= 1)
        {
            this.LOG_ALLOW(credit);
        }

        return new RateLimitDecision
        {
            Allowed = true,
            RetryAfterMs = 0,
            Credit = credit,
            Reason = RateLimitReason.None
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RateLimitDecision CONSUME_TOKEN_AND_CREATE_DECISION(EndpointState state, in RateLimitPolicy policy)
    {
        state.SoftViolations = 0;
        state.MicroBalance -= policy.TokenScale;

        ushort credit = CALCULATE_REMAINING_CREDIT(state.MicroBalance, policy.TokenScale);

        return new RateLimitDecision
        {
            Allowed = true,
            RetryAfterMs = 0,
            Credit = credit,
            Reason = RateLimitReason.None
        };
    }

    private RateLimitDecision HANDLE_INSUFFICIENT_TOKENS(EndpointState state, long now)
    {
        long needed = _options.TokenScale - state.MicroBalance;
        int retryMs = this.CALCULATE_RETRY_DELAY_MS(needed);

        this.RECORD_VIOLATION(state, now);

        // Check if should escalate to hard lock
        return this.SHOULD_ESCALATE_TO_HARD_LOCK(state) ? this.ESCALATE_TO_HARD_LOCK(state, now)
            : new RateLimitDecision
            {
                Allowed = false,
                RetryAfterMs = retryMs,
                Reason = RateLimitReason.SoftThrottle,
                Credit = CALCULATE_REMAINING_CREDIT(state.MicroBalance, _options.TokenScale)
            };
    }

    private RateLimitDecision HANDLE_INSUFFICIENT_TOKENS(EndpointState state, long now, in RateLimitPolicy policy)
    {
        long needed = policy.TokenScale - state.MicroBalance;
        int retryMs = this.CALCULATE_RETRY_DELAY_MS(needed, in policy);

        this.RECORD_VIOLATION(state, now);

        return this.SHOULD_ESCALATE_TO_HARD_LOCK(state, in policy) ? this.ESCALATE_TO_HARD_LOCK(state, now, in policy)
            : new RateLimitDecision
            {
                Allowed = false,
                RetryAfterMs = retryMs,
                Reason = RateLimitReason.SoftThrottle,
                Credit = CALCULATE_REMAINING_CREDIT(state.MicroBalance, policy.TokenScale)
            };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RECORD_VIOLATION(EndpointState state, long now)
    {
        long windowTicks = this.TO_TICKS(_options.SoftViolationWindowSeconds);

        if (now - state.LastViolationSw <= windowTicks)
        {
            state.SoftViolations++;
        }
        else
        {
            state.SoftViolations = 1;
        }

        state.LastViolationSw = now;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SHOULD_ESCALATE_TO_HARD_LOCK(EndpointState state) => state.SoftViolations >= _options.MaxSoftViolations;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SHOULD_ESCALATE_TO_HARD_LOCK(EndpointState state, in RateLimitPolicy policy) => state.SoftViolations >= policy.MaxSoftViolations;

    private RateLimitDecision ESCALATE_TO_HARD_LOCK(EndpointState state, long now)
    {
        state.HardBlockedUntilSw = now + this.TO_TICKS(_options.HardLockoutSeconds);
        state.SoftViolations = 0;

        int retryMs = this.CALCULATE_DELAY_MS(now, state.HardBlockedUntilSw);

        return new RateLimitDecision
        {
            Allowed = false,
            RetryAfterMs = retryMs,
            Credit = CALCULATE_REMAINING_CREDIT(state.MicroBalance, _options.TokenScale),
            Reason = RateLimitReason.HardLockout
        };
    }

    private RateLimitDecision ESCALATE_TO_HARD_LOCK(EndpointState state, long now, in RateLimitPolicy policy)
    {
        state.HardBlockedUntilSw = now + this.TO_TICKS(policy.HardLockoutSeconds);
        state.SoftViolations = 0;

        int retryMs = this.CALCULATE_DELAY_MS(now, state.HardBlockedUntilSw);

        return new RateLimitDecision
        {
            Allowed = false,
            RetryAfterMs = retryMs,
            Credit = CALCULATE_REMAINING_CREDIT(state.MicroBalance, policy.TokenScale),
            Reason = RateLimitReason.HardLockout
        };
    }

    #endregion Rate Limit Evaluation

    #region Token Refill Logic

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void REFILL_TOKENS(long now, EndpointState state)
    {
        /*
         * [High-Precision Refill]
         * We compute tokens to add based on nanoseconds elapsed (Stopwatch ticks).
         * To avoid precision loss during integer division, we store the 
         * 'remainder' nanoseconds in state.AccumulatedMicro and add them 
         * to the next refill cycle.
         */
        long dt = now - state.LastRefillSwTicks;

        if (dt <= 0)
        {
            return;
        }

        // This prevents time drift and ensures consistent refill intervals
        state.LastRefillSwTicks = now;

        double scale = this.GetAdaptiveScale();

        // Check for potential overflow before multiplication
        if (dt * _refillPerTick * scale >= _capacityMicro)
        {
            // Extreme case: very long dt or high refill rate -> cap at full
            state.AccumulatedMicro = 0;
            state.MicroBalance = _capacityMicro;
            return;
        }

        // Early exit if already at capacity (optimization)
        if (state.MicroBalance >= _capacityMicro)
        {
            state.AccumulatedMicro = 0; // Reset accumulator when full
            return;
        }

        // Formula: (dt * refillRate) + accumulated = whole_tokens * frequency + remainder
        long microToAdd = (long)(dt * _refillPerTick * scale) + state.AccumulatedMicro;

        if (microToAdd <= 0)
        {
            return;
        }

        // Store remainder for next refill (prevents precision loss)
        long newBalance = state.MicroBalance + microToAdd;
        state.MicroBalance = newBalance > _capacityMicro ? _capacityMicro : newBalance;

        // Reset accumulator if capped (prevents unbounded growth)
        if (microToAdd > _capacityMicro)
        {
            state.AccumulatedMicro = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void REFILL_TOKENS(long now, EndpointState state, in RateLimitPolicy policy)
    {
        long dt = now - state.LastRefillSwTicks;
        if (dt <= 0)
        {
            return;
        }

        state.LastRefillSwTicks = now;

        double scale = this.GetAdaptiveScale();

        if (dt * policy.RefillPerTick * scale >= policy.CapacityMicro)
        {
            state.AccumulatedMicro = 0;
            state.MicroBalance = policy.CapacityMicro;
            return;
        }

        if (state.MicroBalance >= policy.CapacityMicro)
        {
            state.AccumulatedMicro = 0;
            return;
        }

        long microToAdd = (long)(dt * policy.RefillPerTick * scale) + state.AccumulatedMicro;

        if (microToAdd <= 0)
        {
            return;
        }

        long newBalance = state.MicroBalance + microToAdd;
        state.MicroBalance = newBalance > policy.CapacityMicro ? policy.CapacityMicro : newBalance;

        if (microToAdd > policy.CapacityMicro)
        {
            state.AccumulatedMicro = 0;
        }
    }

    #endregion Token Refill Logic

    #region Time & Calculation Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CALCULATE_RETRY_DELAY_MS(long microNeeded)
    {
        double scale = this.GetAdaptiveScale();
        double refillPerSec = _refillPerSecMicro * scale;
        if (refillPerSec <= 0)
        {
            return 0; // No refill configured
        }

        if (microNeeded <= 0)
        {
            return 0; // No tokens needed
        }

        // Check for potential overflow BEFORE calculation
        if (microNeeded > (long.MaxValue / 1000))
        {
            // Would overflow in multiplication
            return int.MaxValue;
        }

        // Safe calculation with double precision
        double delayMs = microNeeded * 1000.0 / refillPerSec;

        // Clamp to maximum safe value
        if (delayMs >= MaxDelayMs || double.IsInfinity(delayMs) || double.IsNaN(delayMs))
        {
            return int.MaxValue;
        }

        int ms = (int)Math.Ceiling(delayMs);

        // Clamp to [0, Int32.MaxValue]
        return ms < 0 ? 0 : ms;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CALCULATE_RETRY_DELAY_MS(long microNeeded, in RateLimitPolicy policy)
    {
        double scale = this.GetAdaptiveScale();
        double refillPerSec = policy.RefillPerSecMicro * scale;
        if (refillPerSec <= 0 || microNeeded <= 0)
        {
            return 0;
        }

        if (microNeeded > (long.MaxValue / 1000))
        {
            return int.MaxValue;
        }

        double delayMs = microNeeded * 1000.0 / refillPerSec;

        if (delayMs >= MaxDelayMs || double.IsInfinity(delayMs) || double.IsNaN(delayMs))
        {
            return int.MaxValue;
        }

        int ms = (int)Math.Ceiling(delayMs);
        return ms < 0 ? 0 : ms;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CALCULATE_DELAY_MS(long now, long untilSw)
    {
        if (untilSw <= now)
        {
            return 0; // Already passed
        }

        long dtTicks = untilSw - now;

        if (dtTicks is < 0 or > (long.MaxValue / 1000))
        {
            return int.MaxValue;
        }

        double sec = dtTicks / _swFreq;
        long wholeTicks = (long)(sec * _swFreq);
        long remainderTicks = dtTicks - wholeTicks;
        double delayMs = (sec * 1000.0) + (remainderTicks * 1000.0 / _swFreq);

        if (delayMs >= MaxDelayMs || double.IsInfinity(delayMs) || double.IsNaN(delayMs))
        {
            return int.MaxValue;
        }

        int ms = (int)Math.Ceiling(delayMs);

        return ms < 0 ? 0 : ms;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long TO_TICKS(int seconds) => (long)Math.Round(seconds * _swFreq);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort CALCULATE_REMAINING_CREDIT(long microBalance, int tokenScale)
    {
        long tokens = microBalance / tokenScale;

        if (tokens <= 0)
        {
            return 0;
        }
        else
        {
            return tokens >= ushort.MaxValue ? ushort.MaxValue : (ushort)tokens;
        }
    }

    #endregion Time & Calculation Helpers

    #region Shard Selection

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Shard SELECT_SHARD(INetworkEndpoint key)
    {
        uint h;
        Span<char> buffer = stackalloc char[64];

        if (key.TryFormatAddress(buffer, out int written))
        {
            ReadOnlySpan<char> charSpan = buffer[..written];
            ReadOnlySpan<byte> ipBytes = MemoryMarshal.AsBytes(charSpan);
            h = XxHash32.Compute(ipBytes);
        }
        else
        {
#pragma warning disable NALIX072 // INetworkEndpoint lacks zero-allocation address API; deferred until interface is extended.
            ReadOnlySpan<char> charSpan = key.Address.AsSpan();
            ReadOnlySpan<byte> ipBytes = MemoryMarshal.AsBytes(charSpan);
            h = XxHash32.Compute(ipBytes);
#pragma warning restore NALIX072
        }

        // Mix hash for better distribution
        unchecked
        {
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;

            return _shards[(int)(h & (uint)(_shards.Length - 1))];
        }
    }

    #endregion Shard Selection

    #region Initialization


    /// <summary>
    /// Calculates the initial micro-token balance for new endpoints based on configuration.
    /// </summary>
    /// <returns>Initial balance in micro-tokens. </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long CalculateInitialBalance()
    {
        // Default (-1): Start with full capacity
        if (_options.InitialTokens < 0)
        {
            return _capacityMicro;
        }

        // Explicit 0: Start empty (cold-start mode)
        if (_options.InitialTokens == 0)
        {
            return 0;
        }

        // Custom value:  Clamp to [0, capacity]
        long requestedMicro = (long)_options.InitialTokens * _options.TokenScale;
        return Math.Clamp(requestedMicro, 0, _capacityMicro);
    }

    /// <summary>
    /// Calculates the initial micro-token balance for new endpoints based on dynamic policy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long CalculateInitialBalance(in RateLimitPolicy policy)
    {
        if (_options.InitialTokens < 0)
        {
            return policy.CapacityMicro;
        }

        if (_options.InitialTokens == 0)
        {
            return 0;
        }

        long requestedMicro = (long)_options.InitialTokens * policy.TokenScale;
        return Math.Clamp(requestedMicro, 0, policy.CapacityMicro);
    }

    #endregion Initialization

    #region IDisposable & IAsyncDisposable

    /// <inheritdoc />
    /// <summary>
    /// Synchronously disposes the limiter and cancels cleanup job.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cleanupJob?.Dispose();

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog(
                    "RT.TokenBucketLimiter:Dispose",
                    "disposed"));
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Asynchronously disposes the limiter (delegates to sync Dispose).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        this.Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion IDisposable & IAsyncDisposable

    #region Private

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_KEY_ADDRESS_EMPTY() => throw new ArgumentException("Endpoint address cannot be null or empty", "key");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_HARD_BLOCKED(int retryMs)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Trace,
                new DiagnosticLog(
                    "RT.TokenBucketLimiter:Internal",
                    $"hard-blocked retry_ms={retryMs}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_ALLOW(ushort credit)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Trace,
                new DiagnosticLog(
                    "RT.TokenBucketLimiter:Internal",
                    $"allow credit={credit}"));
        }
    }

    #endregion Private
}
