// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Environment.Memory;
using Nalix.Observability.Contracts;
using Nalix.Observability.Internal.History;
using Nalix.Observability.Tests.TestSupport;

namespace Nalix.Observability.Tests.Internal.History;

/// <summary>
/// Verifies the bucketed series, the catalog and the query parsing done by <see cref="HistoryQuery"/>.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class HistoryQueryTests : IDisposable
{
    private static readonly long s_day = HistoryTestSupport.Day;

    private readonly TempHistoryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Series_WhenSamplesSpanBuckets_ReturnsAverageAndMaxWithCarriedValues()
    {
        using (HistoryWriter writer = new(_directory.FullPath))
        {
            // A.Value: 10, 20 in bucket 0; carried 20 then 40 in bucket 1. B.Value written once, carried forward.
            HistoryTestSupport.WriteSample(writer, s_day + 0, ("A.Value", 10), ("B.Value", 7));
            HistoryTestSupport.WriteSample(writer, s_day + 1_000, ("A.Value", 20));
            HistoryTestSupport.WriteSample(writer, s_day + 10_000, ("A.Value", 20), ("C.Other", 1));
            HistoryTestSupport.WriteSample(writer, s_day + 11_000, ("A.Value", 40));
        }

        using HistoryRecorder recorder = _directory.CreateRecorder();
        using BufferLease lease = HistoryQuery.Series(recorder, s_day, s_day + 19_999, 2, ["A.Value", "B.Value", "Missing"]);
        using JsonDocument json = JsonDocument.Parse(lease.Memory);
        JsonElement series = json.RootElement.GetProperty("series");

        Assert.Equal(10_000, json.RootElement.GetProperty("step").GetInt64());
        Assert.Equal([15d, 30d], HistoryTestSupport.Values(series.GetProperty("A.Value").GetProperty("avg")));
        Assert.Equal([20d, 40d], HistoryTestSupport.Values(series.GetProperty("A.Value").GetProperty("max")));
        Assert.Equal([7d, 7d], HistoryTestSupport.Values(series.GetProperty("B.Value").GetProperty("avg")));
        Assert.All(series.GetProperty("Missing").GetProperty("avg").EnumerateArray(), v => Assert.Equal(JsonValueKind.Null, v.ValueKind));
    }

    [Fact]
    public void Catalog_WhenDaysRecorded_ListsDaysAndMetrics()
    {
        using (HistoryWriter writer = new(_directory.FullPath))
        {
            HistoryTestSupport.WriteSample(writer, s_day + 5_000, ("Process.WorkingSetBytes", 1));
            HistoryTestSupport.WriteSample(writer, s_day + 86_400_000 + 5_000, ("Connections.TotalConnections", 2));
        }

        using HistoryRecorder recorder = _directory.CreateRecorder();
        using BufferLease lease = HistoryQuery.Catalog(recorder, s_day, s_day + (2 * 86_400_000));
        using JsonDocument json = JsonDocument.Parse(lease.Memory);

        string[] days = [.. json.RootElement.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("date").GetString()!)];
        string[] metrics = [.. json.RootElement.GetProperty("metrics").EnumerateArray().Select(m => m.GetString()!)];
        Assert.Equal(["2026-10-27", "2026-10-28"], days);
        Assert.Equal(["Connections.TotalConnections", "Process.WorkingSetBytes"], metrics);
        Assert.Equal(10_000, json.RootElement.GetProperty("interval").GetInt64());
    }

    [Fact]
    public void TryParse_WhenQueryIsMalformedOrInverted_ReturnsFalse()
    {
        Assert.False(HistoryQuery.TryParse("not json"u8, out _, out _, out _, out _));
        Assert.False(HistoryQuery.TryParse("""{"from":10,"to":5}"""u8, out _, out _, out _, out _));
    }

    [Fact]
    public void TryParse_WhenQueryIsValid_ClampsPointsAndIgnoresUnknownFields()
    {
        Assert.True(HistoryQuery.TryParse("""{"from":1,"to":5,"points":99999,"metrics":["a","b"],"extra":{"x":1}}"""u8, out long from, out long to, out int points, out List<string> metrics));
        Assert.Equal((1, 5, HistoryQuery.MaxPoints), (from, to, points));
        Assert.Equal(["a", "b"], metrics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public void FitPoints_WhenManyMetricsRequested_KeepsWorstCaseResponseWithinBudget(int metricCount)
    {
        const int Budget = 96 * 1024;

        int points = HistoryQuery.FitPoints(HistoryQuery.MaxPoints, metricCount, Budget);

        // Worst case per bucket: a 15-byte time plus avg and max of 13 bytes each per metric.
        long worstCase = 1024 + (metricCount * 96) + (points * (15 + (metricCount * 2 * 13)));
        Assert.InRange(points, 2, HistoryQuery.MaxPoints);
        Assert.True(worstCase <= Budget, $"{points} points for {metricCount} metrics can reach {worstCase} bytes");
    }

    [Fact]
    public void Catalog_WhenMetricNamesExceedBudget_TruncatesAndFlagsIt()
    {
        using (HistoryWriter writer = new(_directory.FullPath))
        {
            writer.Begin(HistoryTestSupport.Day);
            for (int i = 0; i < 2_000; i++)
            {
                writer.Write("Section.AVeryLongMetricNameThatTakesSpace" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i);
            }

            writer.End();
        }

        using HistoryRecorder recorder = _directory.CreateRecorder();
        using BufferLease lease = HistoryQuery.Catalog(recorder, HistoryTestSupport.Day, HistoryTestSupport.Day + 1_000, 16 * 1024);
        using JsonDocument json = JsonDocument.Parse(lease.Memory);

        Assert.True(lease.Length <= 16 * 1024, $"catalog is {lease.Length} bytes");
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.InRange(json.RootElement.GetProperty(RuntimeObservationHistory.Metrics).GetArrayLength(), 1, 1_999);
    }
}
