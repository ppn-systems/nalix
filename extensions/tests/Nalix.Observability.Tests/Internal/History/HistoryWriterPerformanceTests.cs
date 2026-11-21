// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Nalix.Environment.Memory;
using Nalix.Observability.Internal.History;
using Nalix.Observability.Tests.TestSupport;

namespace Nalix.Observability.Tests.Internal.History;

/// <summary>
/// Verifies the cost of a full day of samples: the day file stays small and a one-day query stays fast.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class HistoryWriterPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly long s_day = HistoryTestSupport.Day;

    private readonly TempHistoryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Write_WhenFullDayAt10sInterval_StaysSmallAndQueriesFast()
    {
        const int Samples = 8_640;
        const int Metrics = 60;
        Stopwatch write = Stopwatch.StartNew();
        using (HistoryWriter writer = new(_directory.FullPath))
        {
            for (int s = 0; s < Samples; s++)
            {
                writer.Begin(s_day + (s * 10_000L));
                for (int m = 0; m < Metrics; m++)
                {
                    // A third of the metrics move every sample, the rest change rarely, like real counters.
                    double value = m % 3 == 0 ? Math.Sin(s / 50.0) * 1000 : s / 500;
                    writer.Write(HistoryTestSupport.MetricName(m), value);
                }

                writer.End();
            }
        }

        write.Stop();
        long bytes = new FileInfo(_directory.DayFile(s_day)).Length;

        using HistoryRecorder recorder = _directory.CreateRecorder();
        Stopwatch query = Stopwatch.StartNew();
        using BufferLease lease = HistoryQuery.Series(recorder, s_day, s_day + 86_400_000, 600,
            [HistoryTestSupport.MetricName(0), HistoryTestSupport.MetricName(1), HistoryTestSupport.MetricName(2), HistoryTestSupport.MetricName(3)]);
        query.Stop();

        output.WriteLine($"day file: {bytes / 1024.0:F1} KiB, write total {write.ElapsedMilliseconds} ms ({write.Elapsed.TotalMicroseconds / Samples:F1} µs/sample), 1-day 4-metric query {query.ElapsedMilliseconds} ms, response {lease.Length} bytes");
        Assert.True(bytes < 3 * 1024 * 1024, $"day file is {bytes} bytes");
        Assert.True(query.ElapsedMilliseconds < 2_000, $"query took {query.ElapsedMilliseconds} ms");
    }
}
