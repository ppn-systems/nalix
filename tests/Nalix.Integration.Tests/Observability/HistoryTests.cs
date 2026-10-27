// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nalix.Environment.Memory;
using Nalix.Observability;
using Nalix.Observability.Contracts;
using Nalix.Observability.Internal.History;

namespace Nalix.Integration.Tests.Observability;

/// <summary>
/// Verifies the on-disk history: change-only writes, torn-tail tolerance, bucketed queries, the catalog,
/// derived rates, retention and the cost of a full day of samples.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class HistoryTests(ITestOutputHelper output) : IDisposable
{
    private static readonly long s_day = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nxh-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Writer_WhenValueUnchanged_WritesItOnlyOnce()
    {
        using (HistoryWriter writer = new(_directory))
        {
            for (int i = 0; i < 3; i++)
            {
                writer.Begin(s_day + (i * 10_000));
                writer.Write("A.Constant", 5);
                writer.Write("A.Counter", i);
                writer.End();
            }
        }

        List<(long Time, int Pairs)> samples = [];
        using HistoryReader reader = new(DayFile());
        Assert.True(reader.IsValid);
        while (reader.Next())
        {
            if (reader.Kind == HistoryFormat.SampleRecord)
            {
                samples.Add((reader.SampleTime, reader.PairCount));
            }
        }

        Assert.Equal([(s_day, 2), (s_day + 10_000, 1), (s_day + 20_000, 1)], samples);
    }

    [Fact]
    public void Reader_WhenTailIsTorn_StopsAtLastCompleteRecord()
    {
        using (HistoryWriter writer = new(_directory))
        {
            writer.Begin(s_day);
            writer.Write("A.Value", 1);
            writer.End();
            writer.Begin(s_day + 1000);
            writer.Write("A.Value", 2);
            writer.End();
        }

        using (FileStream file = new(DayFile(), FileMode.Open))
        {
            file.SetLength(file.Length - 3);
        }

        int samples = 0;
        using HistoryReader reader = new(DayFile());
        while (reader.Next())
        {
            samples += reader.Kind == HistoryFormat.SampleRecord ? 1 : 0;
        }

        Assert.Equal(1, samples);
    }

    [Fact]
    public void Writer_WhenDayFileExists_ContinuesWithSameColumnIds()
    {
        using (HistoryWriter writer = new(_directory))
        {
            writer.Begin(s_day);
            writer.Write("A.First", 1);
            writer.End();
        }

        using (HistoryWriter writer = new(_directory))
        {
            writer.Begin(s_day + 1000);
            writer.Write("A.First", 1);
            writer.Write("A.Second", 2);
            writer.End();
        }

        List<string> columns = [];
        using HistoryReader reader = new(DayFile());
        while (reader.Next())
        {
            if (reader.Kind == HistoryFormat.ColumnRecord)
            {
                columns.Add(reader.ColumnId + ":" + reader.ColumnName);
            }
        }

        Assert.Equal(["0:A.First", "1:A.Second"], columns);
    }

    [Fact]
    public void Series_WhenSamplesSpanBuckets_ReturnsAverageAndMaxWithCarriedValues()
    {
        using (HistoryWriter writer = new(_directory))
        {
            // A.Value: 10, 20 in bucket 0; carried 20 then 40 in bucket 1. B.Value written once, carried forward.
            WRITE(writer, s_day + 0, ("A.Value", 10), ("B.Value", 7));
            WRITE(writer, s_day + 1_000, ("A.Value", 20));
            WRITE(writer, s_day + 10_000, ("A.Value", 20), ("C.Other", 1));
            WRITE(writer, s_day + 11_000, ("A.Value", 40));
        }

        HistoryRecorder recorder = RECORDER();
        using BufferLease lease = HistoryQuery.Series(recorder, s_day, s_day + 19_999, 2, ["A.Value", "B.Value", "Missing"]);
        using JsonDocument json = JsonDocument.Parse(lease.Memory);
        JsonElement series = json.RootElement.GetProperty("series");

        Assert.Equal(10_000, json.RootElement.GetProperty("step").GetInt64());
        Assert.Equal([15d, 30d], VALUES(series.GetProperty("A.Value").GetProperty("avg")));
        Assert.Equal([20d, 40d], VALUES(series.GetProperty("A.Value").GetProperty("max")));
        Assert.Equal([7d, 7d], VALUES(series.GetProperty("B.Value").GetProperty("avg")));
        Assert.All(series.GetProperty("Missing").GetProperty("avg").EnumerateArray(), v => Assert.Equal(JsonValueKind.Null, v.ValueKind));
    }

    [Fact]
    public void Catalog_WhenDaysRecorded_ListsDaysAndMetrics()
    {
        using (HistoryWriter writer = new(_directory))
        {
            WRITE(writer, s_day + 5_000, ("Process.WorkingSetBytes", 1));
            WRITE(writer, s_day + 86_400_000 + 5_000, ("Connections.TotalConnections", 2));
        }

        using BufferLease lease = HistoryQuery.Catalog(RECORDER(), s_day, s_day + (2 * 86_400_000));
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
        Assert.True(HistoryQuery.TryParse("""{"from":1,"to":5,"points":99999,"metrics":["a","b"],"extra":{"x":1}}"""u8, out long from, out long to, out int points, out List<string> metrics));
        Assert.Equal((1, 5, HistoryQuery.MaxPoints), (from, to, points));
        Assert.Equal(["a", "b"], metrics);
    }

    [Fact]
    public void Sample_WhenTwoSamplesTaken_RecordsProcessFieldsAndDerivedRates()
    {
        using HistoryRecorder recorder = RECORDER();
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
        _ = Directory.CreateDirectory(_directory);
        string old = Path.Combine(_directory, HistoryFormat.FileName(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10)));
        string kept = Path.Combine(_directory, HistoryFormat.FileName(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)));
        File.WriteAllBytes(old, HistoryFormat.Magic.ToArray());
        File.WriteAllBytes(kept, HistoryFormat.Magic.ToArray());

        using HistoryRecorder recorder = RECORDER(retentionDays: 3);
        recorder.Sample(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void FullDay_At10sInterval_StaysSmallAndQueriesFast()
    {
        const int Samples = 8_640;
        const int Metrics = 60;
        Stopwatch write = Stopwatch.StartNew();
        using (HistoryWriter writer = new(_directory))
        {
            for (int s = 0; s < Samples; s++)
            {
                writer.Begin(s_day + (s * 10_000L));
                for (int m = 0; m < Metrics; m++)
                {
                    // A third of the metrics move every sample, the rest change rarely, like real counters.
                    double value = m % 3 == 0 ? Math.Sin(s / 50.0) * 1000 : s / 500;
                    writer.Write(NAME(m), value);
                }

                writer.End();
            }
        }

        write.Stop();
        long bytes = new FileInfo(DayFile()).Length;

        HistoryRecorder recorder = RECORDER();
        Stopwatch query = Stopwatch.StartNew();
        using BufferLease lease = HistoryQuery.Series(recorder, s_day, s_day + 86_400_000, 600, [NAME(0), NAME(1), NAME(2), NAME(3)]);
        query.Stop();

        output.WriteLine($"day file: {bytes / 1024.0:F1} KiB, write total {write.ElapsedMilliseconds} ms ({write.Elapsed.TotalMicroseconds / Samples:F1} µs/sample), 1-day 4-metric query {query.ElapsedMilliseconds} ms, response {lease.Length} bytes");
        Assert.True(bytes < 3 * 1024 * 1024, $"day file is {bytes} bytes");
        Assert.True(query.ElapsedMilliseconds < 2_000, $"query took {query.ElapsedMilliseconds} ms");
    }

    private static string NAME(int metric) => "Section.Metric" + metric.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void WRITE(HistoryWriter writer, long time, params (string Name, double Value)[] values)
    {
        writer.Begin(time);
        foreach ((string name, double value) in values)
        {
            writer.Write(name, value);
        }

        writer.End();
    }

    private static double[] VALUES(JsonElement array) => [.. array.EnumerateArray().Select(v => v.GetDouble())];

    private HistoryRecorder RECORDER(int retentionDays = 7) => new(new ObservabilityOptions
    {
        HistoryDirectory = _directory,
        HistoryInterval = TimeSpan.FromSeconds(10),
        HistoryRetentionDays = retentionDays,
        HistoryTargets = RuntimeObservationTargets.PROCESS,
    });

    private string DayFile() => Path.Combine(_directory, HistoryFormat.FileName(HistoryFormat.DayOf(s_day)));
}
