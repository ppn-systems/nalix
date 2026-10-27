// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Framework.Injection;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;

namespace Nalix.Runtime.Throttling;

public sealed partial class TokenBucketLimiter
{
    #region Cleanup

    /// <summary>
    /// Periodic cleanup of stale endpoints to bound memory use.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CLEANUP_STALE_ENDPOINTS()
    {
        if (_disposed)
        {
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        CancellationToken token = cts.Token;

        try
        {
            long now = Stopwatch.GetTimestamp();
            int removed = this.PERFORM_STALE_CLEANUP(now, token);

            removed += this.ENFORCE_LIMIT_IF_NEEDED(token);

            if (removed > 0)
            {
                _ = Interlocked.Add(ref _totalCleaned, removed);

                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(
                        DiagnosticsEvents.Internal.Debug,
                        new DiagnosticLog(
                            "RT.TokenBucketLimiter:Internal",
                            $"cleanup removed={removed}"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog(
                        "RT.TokenBucketLimiter:Internal",
                        "cleanup cancelled-due-to-timeout"));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex) && ex is not ObjectDisposedException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog(
                        "RT.TokenBucketLimiter:Internal",
                        "cleanup-error",
                        ex));
            }
        }
    }

    /// <summary>
    /// Performs cleanup of stale endpoints across all shards.
    /// </summary>
    /// <param name="now"></param>
    /// <param name="token"></param>
    private int PERFORM_STALE_CLEANUP(
        long now,
        CancellationToken token)
    {
        int removed = 0;
        int visited = 0;
        long staleTicks = this.TO_TICKS(_options.StaleEntrySeconds);

        // BUG-25 fix: Rotate shard start index so cleanup doesn't always
        // begin from shard 0. Under flood conditions with a timeout, earlier
        // shards would starve later ones if iteration always starts at 0.
        int shardCount = _shards.Length;
        int startIdx = Interlocked.Increment(ref _cleanupShardStart) % shardCount;

        for (int s = 0; s < shardCount; s++)
        {
            token.ThrowIfCancellationRequested();

            Shard shard = _shards[(startIdx + s) % shardCount];

            foreach (KeyValuePair<INetworkEndpoint, EndpointState> kv in shard.Map)
            {
                visited++;

                if ((visited & (CancellationCheckFrequency - 1)) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                if (this.TRY_REMOVE_STALE_ENDPOINT(kv, now, staleTicks, shard))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Attempts to remove a stale endpoint with double-check pattern.
    /// </summary>
    /// <param name="kv"></param>
    /// <param name="now"></param>
    /// <param name="staleTicks"></param>
    /// <param name="shard"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TRY_REMOVE_STALE_ENDPOINT(
        KeyValuePair<INetworkEndpoint, EndpointState> kv,
        long now, long staleTicks, Shard shard)
    {
        EndpointState state = kv.Value;

        if (now - state.LastSeenSw <= staleTicks)
        {
            return false;
        }

        lock (state.Lock)
        {
            if ((now - state.LastSeenSw) <= staleTicks)
            {
                return false;
            }

            // Staleness check and removal are one step under the state lock, so a request that is evaluating this
            // state either finishes first (and refreshes LastSeenSw) or sees Retired afterwards.
            return this.TRY_RETIRE_ENDPOINT(kv, shard);
        }
    }

    /// <summary>
    /// Removes exactly <paramref name="kv"/> from its shard and marks the state retired.
    /// The caller must hold <c>kv.Value.Lock</c>.
    /// </summary>
    /// <remarks>
    /// The state is intentionally NOT returned to the pool: a request that looked it up just before the removal may
    /// still hold a reference, and recycling it for another endpoint would let that request spend the other
    /// endpoint's tokens. Retired states are simply collected by the GC.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TRY_RETIRE_ENDPOINT(KeyValuePair<INetworkEndpoint, EndpointState> kv, Shard shard)
    {
        if (!shard.Map.TryRemove(kv))
        {
            return false;
        }

        Volatile.Write(ref kv.Value.Retired, 1);
        _ = Interlocked.Decrement(ref _totalEndpointCount);
        return true;
    }

    /// <summary>
    /// Enforces MaxTrackedEndpoints limit if exceeded.
    /// </summary>
    /// <param name="token"></param>
    private int ENFORCE_LIMIT_IF_NEEDED(CancellationToken token)
    {
        if (_options.MaxTrackedEndpoints <= 0)
        {
            return 0;
        }

        int currentCount = Interlocked.CompareExchange(ref _totalEndpointCount, 0, 0);

        if (currentCount <= _options.MaxTrackedEndpoints)
        {
            return 0;
        }

        int toRemove = currentCount - _options.MaxTrackedEndpoints;
        int removed = this.REMOVE_OLDEST_ENDPOINTS(toRemove, token);

        if (removed > 0)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog(
                        "RT.TokenBucketLimiter:Internal",
                        $"evicted count={removed} limit={_options.MaxTrackedEndpoints}"));
            }
        }

        return removed;
    }

    /// <summary>
    /// Evicts the oldest endpoints across all shards.
    /// </summary>
    /// <param name="count"></param>
    /// <param name="cancellationToken"></param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int REMOVE_OLDEST_ENDPOINTS(
        int count,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return 0;
        }

        int removed = 0;
        int visited = 0;
        int shardCount = _shards.Length;
        int startIdx = Volatile.Read(ref _cleanupShardStart);

        long staleTicks = this.TO_TICKS(_options.StaleEntrySeconds);
        long thresholdTicks = staleTicks / 2;
        long now = Stopwatch.GetTimestamp();

        // Pass 0: Evict endpoints idle for more than 50% of stale seconds
        // Pass 1: Evict any non-blocked endpoints until limit is enforced
        for (int pass = 0; pass < 2 && removed < count; pass++)
        {
            for (int s = 0; s < shardCount && removed < count; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Shard shard = _shards[(startIdx + s) % shardCount];

                foreach (KeyValuePair<INetworkEndpoint, EndpointState> kv in shard.Map)
                {
                    visited++;
                    if ((visited & (CancellationCheckFrequency - 1)) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    EndpointState state = kv.Value;
                    bool evicted = false;

                    lock (state.Lock)
                    {
                        bool isBlocked = state.HardBlockedUntilSw > now;
                        if (!isBlocked)
                        {
                            bool shouldEvict = pass != 0 || (now - state.LastSeenSw) > thresholdTicks;
                            evicted = shouldEvict && this.TRY_RETIRE_ENDPOINT(kv, shard);
                        }
                    }

                    if (evicted)
                    {
                        removed++;

                        if (removed >= count)
                        {
                            break;
                        }
                    }
                }
            }
        }

        return removed;
    }

    #endregion Cleanup

    /// <summary>
    /// Schedules the recurring cleanup job.
    /// </summary>
    private void SCHEDULE_CLEANUP_JOB()
    {
        _cleanupJob = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleRecurring(
            name: TaskNaming.Recurring.CleanupJobId(RecurringName, this.GetHashCode()),
            interval: TimeSpan.FromSeconds(_cleanupIntervalSec),
            work: _ =>
            {
                this.CLEANUP_STALE_ENDPOINTS();
                return ValueTask.CompletedTask;
            },
            options: new RecurringOptions
            {
                NonReentrant = true,
                Tag = TaskNaming.Tags.Service,
                Jitter = TimeSpan.FromMilliseconds(250),
                ExecutionTimeout = TimeSpan.FromSeconds(2),
                BackoffCap = TimeSpan.FromSeconds(15)
            }
        );
    }
}

