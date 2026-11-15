// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Environment.Memory;
using Nalix.Observability.Internal.History;
using Nalix.Observability.Tests.TestSupport;

namespace Nalix.Observability.Tests.Internal.History;

/// <summary>
/// Verifies what <see cref="HistoryRecorder"/> records per sample and how it applies retention.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class HistoryRecorderTests : IDisposable
{
    private readonly TempHistoryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Sample_WhenTwoSamplesTaken_RecordsProcessFieldsAndDerivedRates()
    {
        using HistoryRecorder recorder = _directory.CreateRecorder();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        recorder.Sample(now);
        recorder.Sample(now + 1_000);

        using BufferLease lease = HistoryQuery.Catalog(recorder, now - 1, now + 2_000);
        using JsonDocument json = JsonDocument.Parse(lease.Memory);
        string[] metrics = [.. json.RootElement.GetProperty("metrics").EnumerateArray().Select(m => m.GetString()!)];

        Assert.Contains("Process.WorkingSetBytes", metrics);
        Assert.Contains("Process.CpuPercent", metrics);
        Assert.Contains("Process.AllocationBytesPerSecond", metrics);
        Assert.Contains("Process.GcPerSecond", metrics);
        Assert.DoesNotContain(metrics, m => m.Contains('[', StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_WhenDayChanges_DeletesFilesOlderThanRetention()
    {
        _ = Directory.CreateDirectory(_directory.FullPath);
        string old = Path.Combine(_directory.FullPath, HistoryFormat.FileName(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10)));
        string kept = Path.Combine(_directory.FullPath, HistoryFormat.FileName(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)));
        File.WriteAllBytes(old, HistoryFormat.Magic.ToArray());
        File.WriteAllBytes(kept, HistoryFormat.Magic.ToArray());

        using HistoryRecorder recorder = _directory.CreateRecorder(retentionDays: 3);
        recorder.Sample(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
    }
}
