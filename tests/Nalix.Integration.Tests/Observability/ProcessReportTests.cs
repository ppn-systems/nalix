// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Observability.Internal;

namespace Nalix.Integration.Tests.Observability;

/// <summary>
/// Verifies the process counters written by <see cref="ProcessReport"/>.
/// </summary>
public sealed class ProcessReportTests
{
    internal static readonly string[] NumericFields =
    [
        "ProcessorCount", "UptimeMs", "CpuTimeMs", "WorkingSetBytes", "ManagedHeapBytes",
        "HeapSizeBytes", "CommittedBytes", "FragmentedBytes", "TotalAllocatedBytes", "GcPauseTimePercent",
        "GcTotalPauseMs", "Gen0Collections", "Gen1Collections", "Gen2Collections", "ThreadPoolThreads",
        "ThreadPoolPendingItems", "ThreadPoolCompletedItems", "LockContentions", "ActiveTimers",
    ];

    [Fact]
    public void WriteReportData_WhenInvoked_WritesEveryNumericCounter()
    {
        using JsonDocument document = ReportJson.Capture(ProcessReport.Instance);

        foreach (string name in NumericFields)
        {
            Assert.True(document.RootElement.TryGetProperty(name, out JsonElement value), name);
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            Assert.True(value.GetDouble() >= 0, $"{name} must not be negative.");
        }
    }

    [Fact]
    public void WriteReportData_WhenInvoked_WritesRuntimeAndOsDescriptions()
    {
        using JsonDocument document = ReportJson.Capture(ProcessReport.Instance);

        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("Runtime").ValueKind);
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("Runtime").GetString()));
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("Os").ValueKind);
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("Os").GetString()));
    }
}
