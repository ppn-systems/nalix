// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Framework.Options;

/// <summary>
/// Configuration for object pool diagnostics and performance settings.
/// </summary>
[IniComment("Object pool configuration — controls diagnostics, lifetime tracking, and leak detection")]
public sealed partial class ObjectPoolOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>
    /// Enables advanced diagnostics for object pools.
    /// When disabled, overhead is minimized for production performance.
    /// </summary>
    [IniComment("Enable advanced diagnostics (lifetime tracking, p95, suspicious object detection)")]
    public bool EnableDiagnostics { get; set; } = false;

    /// <summary>
    /// Enables per-type traffic metrics for object pools (gets, returns, hit rate, outstanding and peak
    /// outstanding objects). Default off: every thread renting a type updates that type's counters, which cost
    /// more than the pooling itself on a multi-core server. Turn it on to diagnose pool usage or leaks; the
    /// manager-wide totals (<see cref="Memory.Objects.ObjectPoolManager.TotalGetOperations"/> and the like)
    /// are kept either way.
    /// </summary>
    [IniComment("Per-type pool metrics: gets, returns, hit rate, outstanding (off by default: costs throughput on many cores)")]
    public bool EnableMetrics { get; set; }

    /// <summary>
    /// Captures stack traces when objects are rented from the pool.
    /// Extremely expensive; enable only for debugging leaks. Requires EnableDiagnostics=true.
    /// </summary>
    [IniComment("Capture allocation stack traces (expensive, use only for debugging leaks)")]
    public bool CaptureStackTraces { get; set; } = false;

    /// <summary>
    /// Threshold in seconds after which an outstanding object is considered "suspicious".
    /// </summary>
    [IniComment("Threshold in seconds to flag 'suspicious' objects in reports")]
    [ValueRange(0, 3600)]
    public int SuspiciousThresholdSeconds { get; set; } = 30;

    /// <summary>
    /// Enables GC-based leak detection using finalizers.
    /// When enabled, a sentinel is attached to rented objects to report if they are GC'd without being returned.
    /// </summary>
    [IniComment("Enable GC-based leak detection using sentinel finalizers")]
    public bool EnableLeakDetection { get; set; } = false;

    /// <summary>
    /// The number of recent lifetime samples to keep for p95 calculation.
    /// </summary>
    [IniComment("Number of recent samples to keep for percentile (p95) calculation")]
    [ValueRange(16, 1024)]
    public int LifetimeReservoirSize { get; set; } = 64;

    /// <summary>
    /// Enables automatic trimming of object pools (same as BufferPoolManager).
    /// </summary>
    [IniComment("Enable automatic memory trimming for object pools")]
    public bool EnableObjectTrimming { get; set; } = true;

    /// <summary>
    /// Interval between routine trim cycles (minutes).
    /// </summary>
    [IniComment("Trim interval in minutes")]
    [ValueRange(1, 60)]
    public int TrimIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Interval for deep (more aggressive) trim cycles.
    /// </summary>
    [IniComment("Deep trim interval in minutes")]
    [ValueRange(5, 120)]
    public int DeepTrimIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets the base percentage of capacity to keep during normal maintenance cycles.
    /// Default value is 75% (trims 25%).
    /// </summary>
    [IniComment("Base keep percentage for normal cycles (default ~75%)")]
    [ValueRange(50, 90)]
    public int BaseKeepPercentage { get; set; } = 75;

    /// <summary>
    /// Gets or sets the percentage of capacity to keep during deep/aggressive cleanup cycles.
    /// Default value is 25% (trims 75%).
    /// </summary>
    [IniComment("Deep keep percentage (aggressive cycle, default ~25%)")]
    [ValueRange(10, 45)]
    public int DeepTrimPercentage { get; set; } = 25;

    /// <summary>
    /// Fraction of excess objects to remove per trim cycle (0.0–1.0).
    /// Lower values make trimming more gradual (e.g., 0.5 = remove half the excess each cycle).
    /// Default is 0.5 (exponential decay).
    /// </summary>
    [IniComment("Fraction of excess to trim per cycle (0.1–1.0, default 0.5)")]
    [ValueRange(0.1, 1.0)]
    public double TrimDecayFactor { get; set; } = 0.5;

    /// <summary>
    /// Gets or sets the hit rate threshold that marks a pool as "hot".
    /// Hot pools are trimmed less aggressively to preserve performance.
    /// Default value is 85%.
    /// </summary>
    [IniComment("Hit rate threshold to be considered 'Hot' pool (light trim)")]
    [ValueRange(75.0, 98.0)]
    public double HotHitRateThreshold { get; set; } = 85.0;

    /// <summary>
    /// Gets or sets the minimum number of objects that should always remain in a pool.
    /// This prevents excessive trimming and reduces allocation spikes.
    /// Default value is 8.
    /// </summary>
    [IniComment("Minimum objects to keep in any pool (safety floor)")]
    [ValueRange(4, 64)]
    public int MinimumKeepObjects { get; set; } = 8;

    /// <summary>
    /// Gets or sets the default maximum capacity for newly created object pools.
    /// Default value is 2048. This applies to pools like BufferLease and ConnectionEventArgs.
    /// </summary>
    [IniComment("Default max capacity for any dynamically created pool (default 2048)")]
    [ValueRange(1024, 1_000_000)]
    public int DefaultMaxPoolSize { get; set; } = 2048;

    /// <summary>
    /// Number of instances to preallocate when a new object type pool is created.
    /// </summary>
    [IniComment("Objects to warm up for each newly created type pool (default 8)")]
    [ValueRange(0, 1_000_000)]
    public int DefaultPreallocate { get; set; } = 8;

    /// <summary>
    /// Maximum number of objects held in per-thread cache slots, per pooled type.
    /// Set to <c>0</c> (default) to disable thread-local caching entirely.
    /// <para>
    /// WARNING: Do not enable in highly asynchronous environments (async/await) using
    /// the ThreadPool. Thread-local caches keep objects on the thread that returned them,
    /// so a continuation that resumes on a different thread will miss the cache.
    /// This leads to objects being stranded on idle threads and inaccurate
    /// <c>AvailableCount</c> reporting.
    /// </para>
    /// </summary>
    [IniComment("Max thread-local slots per type per thread. Keep at 0 (disabled) for async/await workloads to prevent object stranding.")]
    [ValueRange(0, 4)]
    public int ThreadCacheDepth { get; set; } = 0;

    /// <summary>
    /// Validates the configuration options and throws an exception if validation fails.
    /// </summary>
    /// <exception cref="Abstractions.Exceptions.ValidationException">
    /// Thrown when one or more validation attributes fail.
    /// </exception>
    public void Validate() => this.ValidateDataAnnotations();
}
