// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Framework.Memory.Pools;

namespace Nalix.Framework.Memory.Objects;

public sealed partial class ObjectPoolManager
{
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TRIM_EXCESS_OBJECTS()
    {
        // Increment trim cycle counter (used for deep trim scheduling)
        int cycle = Interlocked.Increment(ref _trimCycleCount);
        bool isDeepTrim = this.SHOULD_RUN_DEEP_TRIM(cycle);

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Memory.PoolTrimmed))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Memory.PoolTrimmed, new DiagnosticLog("FW.ObjectPoolManager:Internal",
                $"pool-trimmed cycle={cycle} deep-trim={isDeepTrim.ToString().ToLowerInvariant()} phase=ObjectTrimRun"));
        }

        int totalRemoved = 0;

        // Take a snapshot to safely iterate while trimming (prevents CollectionModifiedException)
        foreach (KeyValuePair<Type, ObjectPool> kvp in _poolDict.ToArray())
        {
            Type type = kvp.Key;
            if (!_metricsDict.TryGetValue(type, out PoolMetrics? metrics))
            {
                continue;
            }

            int trimPercentage = this.CALCULATE_PER_TYPE_TRIM_PERCENTAGE(type, metrics, isDeepTrim);

            try
            {
                int removed = kvp.Value.Trim(trimPercentage, _config.TrimDecayFactor);
                totalRemoved += removed;
                if (removed > 0)
                {
                    _ = Interlocked.Add(ref metrics.TrimCount, removed);
                }

                // Update trim snapshot for next cycle's hit rate calculation
                _ = Interlocked.Exchange(ref metrics.LastTrimGets, metrics.TotalGets);
                _ = Interlocked.Exchange(ref metrics.LastTrimHits, metrics.CacheHits);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                // One pool failing must not crash the entire trim job
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Memory.PoolFailure))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Memory.PoolFailure, new DiagnosticLog("FW.ObjectPoolManager:Internal",
                        $"pool-failure type={type.Name} phase=TrimSinglePool", ex));
                }
            }
        }

        if (totalRemoved > 0)
        {
            _ = Interlocked.Add(ref _totalTrimmedObjects, totalRemoved);

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Memory.PoolTrimmed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Memory.PoolTrimmed, new DiagnosticLog("FW.ObjectPoolManager:Internal",
                    $"pool-trimmed cycle={cycle} deep-trim={isDeepTrim.ToString().ToLowerInvariant()} removed={totalRemoved}"));
            }
        }

        _ = this.PerformHealthCheck();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SHOULD_RUN_DEEP_TRIM(int cycle)
    {
        // Deep trim runs less frequently than normal trim (e.g. every 6 normal cycles if DeepTrimIntervalMinutes = 30)
        int deepEvery = Math.Max(1, _config.DeepTrimIntervalMinutes / Math.Max(1, _config.TrimIntervalMinutes));
        return cycle % deepEvery == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CALCULATE_PER_TYPE_TRIM_PERCENTAGE(Type type, PoolMetrics metrics, bool isDeepTrim)
    {
        long windowGets = metrics.TotalGets - Interlocked.Read(ref metrics.LastTrimGets);
        long windowHits = metrics.CacheHits - Interlocked.Read(ref metrics.LastTrimHits);

        // No gets in this window: the pool is idle (handled below, after the safety floor).
        double hitRate = windowGets > 0
            ? (double)windowHits / windowGets * 100.0
            : 100.0;

        // Get current pool state (available count and capacity) directly without allocating a Dictionary
        int available = 0;
        int maxCap = _defaultMaxPoolSize;
        if (_poolDict.TryGetValue(type, out ObjectPool? pool))
        {
            maxCap = pool.GetMaxCapacity(type);
            available = pool.AvailableCountByType(type);
        }

        // === SAFETY FLOOR ===
        // Never trim below this threshold to prevent excessive churn and keep recovery fast. It covers the
        // working set of the recent past, not of the pool's whole life: the lifetime peak never falls, so one
        // burst kept 1.5x its size in every idle pool for good. The recent peak is this interval's peak, or half
        // the previous one, whichever is larger; an idle pool sheds the burst over a few intervals.
        long peakOutstanding = UPDATE_TRIM_PEAK(metrics);
        int minKeep = Math.Max(
            _config.MinimumKeepObjects,
            Math.Max(maxCap / 12, (int)(peakOutstanding * 1.5))
        );

        if (available <= minKeep)
        {
            return 0; // already at minimum safe level
        }

        double freeRatio = maxCap > 0 ? (double)available / maxCap : 0.0;

        // === IDLE POOL (no Get for a whole trim interval) → shrink toward the safety floor ===
        // The 100 % hit rate assumed above for an empty window made an idle pool look hot, so it kept up to 90 %
        // of its capacity for good. Trim keeps a percentage of the capacity: aim at the floor; the decay factor
        // still takes only part of the excess per cycle.
        if (windowGets == 0)
        {
            return maxCap > 0 ? Math.Clamp((int)Math.Ceiling(minKeep * 100.0 / maxCap), 1, 100) : 0;
        }

        // === HOT POOL (high hit rate) → keep more objects ===
        if (hitRate >= _config.HotHitRateThreshold)
        {
            return Math.Min(90, _config.BaseKeepPercentage + 15); // light trim (keeps e.g. 90%)
        }

        // === COLD / UNHEALTHY / IDLE POOL ===
        bool needsAggressive = hitRate < (_config.HotHitRateThreshold - 20.0) || freeRatio > 0.78 || metrics.ConsecutiveFailures > 2;

        if (needsAggressive)
        {
            // Aggressive trim when cache is poor or too many idle objects
            return Math.Max(_config.DeepTrimPercentage, _config.BaseKeepPercentage - 25);
        }

        if (isDeepTrim)
        {
            // If we reach here during a deep trim (not hot, not completely cold), apply deep trim
            return _config.DeepTrimPercentage;
        }

        // Normal routine trim
        return _config.BaseKeepPercentage;
    }

    /// <summary>
    /// Advances the recent peak of outstanding objects that the trim floor protects, and returns it.
    /// </summary>
    /// <remarks>
    /// The outstanding count is sampled now from the per-type Gets and Returns, which also raises the lifetime peak,
    /// so the working set out at a trim is protected even when no miss sampled it.
    /// </remarks>
    private static long UPDATE_TRIM_PEAK(PoolMetrics metrics)
    {
        metrics.ObserveActivity();
        long outstanding = metrics.SamplePeak();
        long lifetimePeak = Interlocked.Read(ref metrics.PeakOutstanding);

        long previousLifetimePeak = Interlocked.Exchange(ref metrics.LastTrimLifetimePeak, lifetimePeak);
        long intervalPeak = lifetimePeak > previousLifetimePeak ? lifetimePeak : outstanding;
        long recent = Math.Max(intervalPeak, Interlocked.Read(ref metrics.TrimPeak) / 2);
        _ = Interlocked.Exchange(ref metrics.TrimPeak, recent);
        return recent;
    }
}
