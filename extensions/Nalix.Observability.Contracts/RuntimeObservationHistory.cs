// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Observability.Contracts;

/// <summary>
/// JSON property names of the recorded-history exchange
/// (<see cref="RuntimeObservationStage.HISTORY_REQUEST"/> / <see cref="RuntimeObservationStage.HISTORY_RESPONSE"/>).
/// </summary>
/// <remarks>
/// <para>Request: <c>{"from":ms,"to":ms,"points":600,"metrics":["Process.WorkingSetBytes", …]}</c>, times in Unix
/// milliseconds. Omitting <c>metrics</c> (or sending an empty list) asks for the catalog instead.</para>
/// <para>Catalog response: <c>{"interval":ms,"days":[{"date":"2026-10-27","from":ms,"to":ms,"samples":n,"bytes":n}],
/// "metrics":["…"]}</c> — the recorded days overlapping the range and every metric name recorded in them.</para>
/// <para>Series response: <c>{"from":ms,"to":ms,"step":ms,"t":[ms,…],"series":{"name":{"avg":[v|null,…],
/// "max":[v|null,…]}}}</c>. The range is cut into at most <c>points</c> buckets of <c>step</c> milliseconds;
/// <c>t</c> holds each bucket's start and a bucket without samples is <c>null</c>.</para>
/// <para>Metric names are dot paths of numeric report fields (<c>Connections.TotalConnections</c>) plus the derived
/// <c>Process.CpuPercent</c>, <c>Process.AllocationBytesPerSecond</c> and <c>Process.GcPerSecond</c>.</para>
/// </remarks>
public static class RuntimeObservationHistory
{
    /// <summary>Range start, Unix milliseconds.</summary>
    public const string From = "from";

    /// <summary>Range end, Unix milliseconds.</summary>
    public const string To = "to";

    /// <summary>Maximum number of buckets per series.</summary>
    public const string Points = "points";

    /// <summary>Requested metric names; absent or empty for the catalog.</summary>
    public const string Metrics = "metrics";

    /// <summary>Recording interval in milliseconds (catalog).</summary>
    public const string Interval = "interval";

    /// <summary>Recorded days (catalog).</summary>
    public const string Days = "days";

    /// <summary>Bucket width in milliseconds (series).</summary>
    public const string Step = "step";

    /// <summary>Bucket start times (series).</summary>
    public const string Times = "t";

    /// <summary>Per-metric bucket values (series).</summary>
    public const string Series = "series";

    /// <summary>Bucket averages.</summary>
    public const string Average = "avg";

    /// <summary>Bucket maxima.</summary>
    public const string Maximum = "max";
}
