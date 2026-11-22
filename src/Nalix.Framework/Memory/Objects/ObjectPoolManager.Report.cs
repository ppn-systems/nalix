// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Nalix.Framework.Extensions;
using Nalix.Framework.Memory.Internal.PoolTypes;
using Nalix.Framework.Memory.Pools;

namespace Nalix.Framework.Memory.Objects;

public sealed partial class ObjectPoolManager
{
    /// <summary>
    /// Generates a comprehensive report on the current state of all pools with detailed metrics.
    /// </summary>
    /// <returns>A string containing the detailed report.</returns>
    public string GenerateReport()
    {
        StringBuilder sb = new(4096);

        // Header
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] ObjectPoolManager Status:");
        _ = sb.AppendLine();

        // Overall Statistics
        _ = sb.AppendLine("======================================================================");
        _ = sb.AppendLine("Overall Statistics");
        _ = sb.AppendLine("======================================================================");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Last Heal              : {_lastHealthCheckUtc} Ticks");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Uptime                 : {this.Uptime.TotalHours:F2} hours ({this.Uptime.TotalSeconds:F0}s)");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Pools            : {this.PoolCount} (Peak: {this.PeakPoolCount})");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Unhealthy Pools        : {this.UnhealthyPoolCount}");
        _ = sb.AppendLine();

        // Operation Statistics
        _ = sb.AppendLine("Operation Statistics:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Get Operations   : {this.TotalGetOperations:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Return Operations: {this.TotalReturnOperations:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Net Objects            : {this.TotalGetOperations - this.TotalReturnOperations:N0}");

        double uptimeSec = this.Uptime.TotalSeconds;
        if (uptimeSec > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Throughput             : {this.TotalGetOperations / uptimeSec:F1} ops/s");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Creation Rate          : {Interlocked.Read(ref _totalCreated) / uptimeSec:F1} objects/s");
        }

        if (_config.EnableDiagnostics && _config.EnableLeakDetection)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"GC Leak Detected       : {PoolSentinel.TotalLeaked:N0} objects");
        }
        _ = sb.AppendLine();

        // Cache Performance
        _ = sb.AppendLine("Cache Performance:");
        long totalOps = this.TotalGetOperations;
        if (totalOps > 0)
        {
            double hitRate = this.TotalCacheHits / (double)totalOps * 100.0;
            double missRate = this.TotalCacheMisses / (double)totalOps * 100.0;
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Cache Hits             : {this.TotalCacheHits:N0} ({hitRate:F2}%)");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Cache Misses           : {this.TotalCacheMisses:N0} ({missRate:F2}%)");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Overall Hit Rate       : {hitRate:F2}%");
        }
        else
        {
            _ = sb.AppendLine("Cache Hits             : 0 (0.00%)");
            _ = sb.AppendLine("Cache Misses           : 0 (0.00%)");
            _ = sb.AppendLine("Overall Hit Rate       : N/A");
        }
        _ = sb.AppendLine();

        // Configuration
        _ = sb.AppendLine("Configuration:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Default Max s_pool Size: {_defaultMaxPoolSize}");
        _ = sb.AppendLine();

        // Pool Details
        _ = sb.AppendLine("========================================================================================================================");
        _ = sb.AppendLine("Object Details (Dashboard):");
        _ = sb.AppendLine("========================================================================================================================");
        _ = sb.AppendLine("TYPE                         | STORAGE (A/M)     | USAGE (O/P)       | TRAFFIC (G/R)     | FALLBACK | TRIMMED | HIT%   | STATUS");
        _ = sb.AppendLine("-----------------------------+-------------------+-------------------+-------------------+----------+---------+--------+-------");

        // Fix: create sortable list from dictionary
        List<KeyValuePair<Type, ObjectPool>> sortedPools = [.. _poolDict];
        sortedPools.Sort((a, b) => string.CompareOrdinal(a.Key.Name, b.Key.Name));

        foreach (KeyValuePair<Type, ObjectPool> kvp in sortedPools)
        {
            Type type = kvp.Key;
            Dictionary<string, object> typeInfo = kvp.Value.GetTypeInfoByType(kvp.Key);

            string typeName = ReportExtensions.FormatTypeName(type.Name, 28);

            int maxCap = Convert.ToInt32(typeInfo["MaxCapacity"], CultureInfo.InvariantCulture);
            int available = Convert.ToInt32(typeInfo["AvailableCount"], CultureInfo.InvariantCulture);

            long gets = 0, returns = 0, peak = 0, active = 0, misses = 0, trimmed = 0;
            double hitPercent = 0.0;
            string status = "OK";

            if (_metricsDict.TryGetValue(type, out PoolMetrics? metrics))
            {
                metrics.ObserveActivity();
                _ = metrics.SamplePeak();

                gets = metrics.TotalGets;
                returns = metrics.TotalReturns;
                peak = metrics.PeakOutstanding;
                active = metrics.Outstanding;
                misses = metrics.CacheMisses;
                trimmed = metrics.TrimCount;
                hitPercent = gets > 0 ? (metrics.CacheHits / (double)gets * 100.0) : 0.0;

                string poolStatus = this.GET_POOL_STATUS(type, metrics);
                status = poolStatus == "Unhealthy" ? "⚠ FAIL" : poolStatus;
            }

            string storage = ReportExtensions.FormatGroup(available, maxCap, compact: true);
            string usage = ReportExtensions.FormatGroup(active, peak, compact: true);
            string traffic = ReportExtensions.FormatGroup(gets, returns, compact: true);

            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{typeName} | {storage,-17} | {usage,-17} | {traffic,-17} | {misses.FormatCompact(),-8} | {trimmed.FormatCompact(),-7} | {hitPercent,5:F1}% | {status}");

            if (_config.EnableDiagnostics && metrics != null && metrics.TotalReturns > 0)
            {
                double avgMs = metrics.TotalLifetimeTicks / (double)metrics.TotalReturns / Stopwatch.Frequency * 1000.0;
                double maxMs = metrics.MaxLifetimeTicks / (double)Stopwatch.Frequency * 1000.0;
                double p95Ms = this.CALCULATE_P95(metrics);
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"                             | Lifetime (ms): Avg={avgMs:F2}, p95={p95Ms:F2}, Max={maxMs:F2}");
            }
        }

        _ = sb.AppendLine("----------------------------------------------------------------------------------------------------------");
        _ = sb.AppendLine();

        // Suspicious Objects Section
        if (_config.EnableDiagnostics)
        {
            this.AppendSuspiciousObjects(sb);
        }

        // Pool Health Details
        if (this.UnhealthyPoolCount > 0)
        {
            _ = sb.AppendLine("Unhealthy Pools:");
            _ = sb.AppendLine("----------------------------------------------------------------------");
            _ = sb.AppendLine("TYPE                     | Consecutive Failures | Last Access");
            _ = sb.AppendLine("-------------------------+----------------------+---------------------");

            foreach (KeyValuePair<Type, PoolMetrics> kvp in _metricsDict)
            {
                if (kvp.Value.ConsecutiveFailures < 2)
                {
                    continue;
                }

                string typeName = kvp.Key.Name.Length > 24
                    ? $"{kvp.Key.Name.AsSpan(0, 21)}..."
                    : kvp.Key.Name.PadRight(24);

                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{typeName} | {kvp.Value.ConsecutiveFailures,20} | {kvp.Value.LastAccessUtc:HH:mm:ss}");
            }

            _ = sb.AppendLine("----------------------------------------------------------------------");
            _ = sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <inheritdoc/>
    public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString("UtcNow", DateTime.UtcNow);
        writer.WriteNumber("UptimeSeconds", this.Uptime.TotalSeconds);
        writer.WriteNumber(nameof(this.PoolCount), this.PoolCount);
        writer.WriteNumber(nameof(this.PeakPoolCount), this.PeakPoolCount);
        writer.WriteNumber(nameof(this.UnhealthyPoolCount), this.UnhealthyPoolCount);
        writer.WriteNumber("DefaultMaxPoolSize", _defaultMaxPoolSize);
        writer.WriteString("StartTime", _startTime);
        writer.WriteNumber("LastHealthCheckTicks", _lastHealthCheckUtc);
        writer.WriteNumber(nameof(this.TotalGetOperations), this.TotalGetOperations);
        writer.WriteNumber(nameof(this.TotalReturnOperations), this.TotalReturnOperations);
        writer.WriteNumber(nameof(this.TotalCacheHits), this.TotalCacheHits);
        writer.WriteNumber(nameof(this.TotalCacheMisses), this.TotalCacheMisses);
        writer.WriteNumber("TotalCreated", Interlocked.Read(ref _totalCreated));
        writer.WriteNumber("TotalDisposed", Interlocked.Read(ref _totalDisposed));
        writer.WriteNumber("TotalLeaked", PoolSentinel.TotalLeaked);
        writer.WriteNumber(nameof(this.CacheHitRate), this.CacheHitRate);
        writer.WriteNumber("Throughput", this.Uptime.TotalSeconds > 0 ? this.TotalGetOperations / this.Uptime.TotalSeconds : 0);
        writer.WriteNumber("CreationRate", this.Uptime.TotalSeconds > 0 ? Interlocked.Read(ref _totalCreated) / this.Uptime.TotalSeconds : 0);
        writer.WriteNumber("NetObjects", this.TotalGetOperations - this.TotalReturnOperations);

        List<KeyValuePair<Type, ObjectPool>> sortedPools = new(_poolDict.Count);
        foreach (KeyValuePair<Type, ObjectPool> kvp in _poolDict)
        {
            sortedPools.Add(kvp);
        }

        sortedPools.Sort((a, b) => string.CompareOrdinal(a.Key.Name, b.Key.Name));

        writer.WriteStartArray("Pools");
        foreach (KeyValuePair<Type, ObjectPool> kvp in sortedPools)
        {
            Dictionary<string, object> poolInfo = kvp.Value.GetTypeInfoByType(kvp.Key);

            writer.WriteStartObject();
            writer.WriteString("Type", kvp.Key.FullName ?? kvp.Key.Name);
            writer.WriteNumber("Available", poolInfo.TryGetValue("AvailableCount", out object? available) ? Convert.ToInt32(available, CultureInfo.InvariantCulture) : 0);
            writer.WriteNumber("MaxCapacity", poolInfo.TryGetValue("MaxCapacity", out object? maxcap) ? Convert.ToInt32(maxcap, CultureInfo.InvariantCulture) : _defaultMaxPoolSize);
            writer.WriteBoolean("IsActive", !poolInfo.TryGetValue("IsActive", out object? active) || Convert.ToBoolean(active, CultureInfo.InvariantCulture));

            if (_metricsDict.TryGetValue(kvp.Key, out PoolMetrics? metrics))
            {
                metrics.ObserveActivity();
                _ = metrics.SamplePeak();

                long gets = metrics.TotalGets, hits = metrics.CacheHits, misses = metrics.CacheMisses;
                double hitPercent = gets > 0 ? (hits / (double)gets * 100.0) : 0.0;

                writer.WriteNumber("Gets", gets);
                writer.WriteNumber("Hits", hits);
                writer.WriteNumber("Misses", misses);
                writer.WriteNumber("Trimmed", metrics.TrimCount);
                writer.WriteNumber("Dropped", metrics.TotalDropped);
                writer.WriteNumber("HitRate", hitPercent);
                writer.WriteString("LastAccessUtc", metrics.LastAccessUtc);
                writer.WriteString("LastAccessType", metrics.LastAccessType ?? "None");
                writer.WriteNumber("Outstanding", metrics.Outstanding);
                writer.WriteNumber("PeakOutstanding", metrics.PeakOutstanding);
                writer.WriteNumber("ConsecutiveFailures", metrics.ConsecutiveFailures);
                writer.WriteString("Status", this.GET_POOL_STATUS(kvp.Key, metrics));
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        if (this.UnhealthyPoolCount > 0)
        {
            writer.WriteStartArray("UnhealthyPools");
            foreach (KeyValuePair<Type, PoolMetrics> kvp in _metricsDict)
            {
                if (kvp.Value.ConsecutiveFailures < 2)
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("Type", kvp.Key.FullName ?? kvp.Key.Name);
                writer.WriteNumber("ConsecutiveFailures", kvp.Value.ConsecutiveFailures);
                writer.WriteString("LastAccessUtc", kvp.Value.LastAccessUtc);
                writer.WriteNumber("Outstanding", kvp.Value.Outstanding);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }


    private void AppendSuspiciousObjects(StringBuilder sb)
    {
        _ = sb.AppendLine("Suspicious Objects (Outstanding > " + _config.SuspiciousThresholdSeconds + "s):");
        _ = sb.AppendLine("----------------------------------------------------------------------------------------------");
        _ = sb.AppendLine("TYPE                     | Elapsed (s) | Stack Trace (first line)");
        _ = sb.AppendLine("----------------------------------------------------------------------------------------------");

        long now = Stopwatch.GetTimestamp();
        long thresholdTicks = _config.SuspiciousThresholdSeconds * Stopwatch.Frequency;
        int found = 0;

        // We prune stale references while scanning to prevent the bag from growing indefinitely.
        // Since ConcurrentBag is not easily pruned, we'll collect survivors and re-populate
        // ONLY if the bag has grown significantly (e.g. > 1000 items).
        List<WeakReference<PoolSentinel>> survivors = new();

        foreach (WeakReference<PoolSentinel> weakRef in _sentinelTracker)
        {
            if (weakRef.TryGetTarget(out PoolSentinel? sentinel))
            {
                if (sentinel.IsReturned)
                {
                    continue;
                }

                survivors.Add(weakRef);

                long elapsed = now - sentinel.RentTimestamp;
                if (elapsed >= thresholdTicks)
                {
                    found++;
                    double elapsedSec = elapsed / (double)Stopwatch.Frequency;

                    string typeName = sentinel.ObjectType.Name.Length > 24
                        ? $"{sentinel.ObjectType.Name.AsSpan(0, 21)}..."
                        : sentinel.ObjectType.Name.PadRight(24);

                    string stack = "N/A (CaptureStackTraces=false)";
                    if (!string.IsNullOrEmpty(sentinel.StackTrace))
                    {
                        int firstLineEnd = sentinel.StackTrace.IndexOf('\n', StringComparison.Ordinal);
                        stack = firstLineEnd > 0 ? sentinel.StackTrace[..firstLineEnd].Trim() : sentinel.StackTrace;
                    }

                    if (found <= 20)
                    {
                        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{typeName} | {elapsedSec,11:F1} | {stack}");
                    }
                }
            }
        }

        if (_sentinelTracker.Count > 10000 && survivors.Count < _sentinelTracker.Count * 0.7)
        {
            ConcurrentBag<WeakReference<PoolSentinel>> newBag = new();
            foreach (WeakReference<PoolSentinel> wr in survivors)
            {
                newBag.Add(wr);
            }

            _sentinelTracker = newBag;
        }

        // Pruning: If the bag is much larger than current survivors, we might want to reset it.
        // For simplicity in this diagnostic path, we'll just show the count.
        if (found > 20)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"... and {found - 20} more suspicious objects.");
        }

        if (found == 0)
        {
            _ = sb.AppendLine("(None detected)");
        }

        _ = sb.AppendLine("----------------------------------------------------------------------------------------------");
        _ = sb.AppendLine();
    }

}

