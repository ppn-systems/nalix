// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nalix.Abstractions;

namespace Nalix.Observability.Internal;

/// <summary>
/// Reports process-wide counters: CPU time, memory, GC and thread pool.
/// </summary>
/// <remarks>
/// Counters are cumulative where the runtime exposes them that way (CPU time, allocations, collections);
/// clients derive rates from two consecutive samples, which keeps the server stateless.
/// </remarks>
internal sealed class ProcessReport : IReportable
{
    public static readonly ProcessReport Instance = new();

    private static readonly long s_startedAt = GET_START_TIME();

    private ProcessReport()
    {
    }

    public string GenerateReport() => string.Empty;

    public void WriteReportData(Utf8JsonWriter writer)
    {
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        writer.WriteStartObject();
        writer.WriteString("Runtime", RuntimeInformation.FrameworkDescription);
        writer.WriteString("Os", RuntimeInformation.OSDescription);
        writer.WriteNumber("ProcessorCount", System.Environment.ProcessorCount);
        writer.WriteNumber("UptimeMs", now - s_startedAt);
        writer.WriteNumber("CpuTimeMs", (long)System.Environment.CpuUsage.TotalTime.TotalMilliseconds);
        writer.WriteNumber("WorkingSetBytes", System.Environment.WorkingSet);
        writer.WriteNumber("ManagedHeapBytes", GC.GetTotalMemory(forceFullCollection: false));
        writer.WriteNumber("HeapSizeBytes", gc.HeapSizeBytes);
        writer.WriteNumber("CommittedBytes", gc.TotalCommittedBytes);
        writer.WriteNumber("FragmentedBytes", gc.FragmentedBytes);
        writer.WriteNumber("TotalAllocatedBytes", GC.GetTotalAllocatedBytes(precise: false));
        writer.WriteNumber("GcPauseTimePercent", gc.PauseTimePercentage);
        writer.WriteNumber("GcTotalPauseMs", (long)GC.GetTotalPauseDuration().TotalMilliseconds);
        writer.WriteNumber("Gen0Collections", GC.CollectionCount(0));
        writer.WriteNumber("Gen1Collections", GC.CollectionCount(1));
        writer.WriteNumber("Gen2Collections", GC.CollectionCount(2));
        writer.WriteNumber("ThreadPoolThreads", ThreadPool.ThreadCount);
        writer.WriteNumber("ThreadPoolPendingItems", ThreadPool.PendingWorkItemCount);
        writer.WriteNumber("ThreadPoolCompletedItems", ThreadPool.CompletedWorkItemCount);
        writer.WriteNumber("LockContentions", Monitor.LockContentionCount);
        writer.WriteNumber("ActiveTimers", Timer.ActiveCount);
        writer.WriteEndObject();
    }

    private static long GET_START_TIME()
    {
        using Process process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
    }
}
