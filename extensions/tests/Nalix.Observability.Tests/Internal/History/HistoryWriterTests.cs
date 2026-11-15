// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Observability.Internal.History;
using Nalix.Observability.Tests.TestSupport;

namespace Nalix.Observability.Tests.Internal.History;

/// <summary>
/// Verifies what <see cref="HistoryWriter"/> persists: change-only sample writes and stable column ids across reopen.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class HistoryWriterTests : IDisposable
{
    private static readonly long s_day = HistoryTestSupport.Day;

    private readonly TempHistoryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Write_WhenValueUnchanged_WritesItOnlyOnce()
    {
        using (HistoryWriter writer = new(_directory.FullPath))
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
        using HistoryReader reader = new(_directory.DayFile(s_day));
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
    public void Write_WhenDayFileExists_ContinuesWithSameColumnIds()
    {
        using (HistoryWriter writer = new(_directory.FullPath))
        {
            writer.Begin(s_day);
            writer.Write("A.First", 1);
            writer.End();
        }

        using (HistoryWriter writer = new(_directory.FullPath))
        {
            writer.Begin(s_day + 1000);
            writer.Write("A.First", 1);
            writer.Write("A.Second", 2);
            writer.End();
        }

        List<string> columns = [];
        using HistoryReader reader = new(_directory.DayFile(s_day));
        while (reader.Next())
        {
            if (reader.Kind == HistoryFormat.ColumnRecord)
            {
                columns.Add(reader.ColumnId + ":" + reader.ColumnName);
            }
        }

        Assert.Equal(["0:A.First", "1:A.Second"], columns);
    }
}
