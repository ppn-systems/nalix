// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Observability.Internal.History;
using Nalix.Observability.Tests.TestSupport;

namespace Nalix.Observability.Tests.Internal.History;

/// <summary>
/// Verifies how <see cref="HistoryReader"/> walks a day file and tolerates a torn tail.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class HistoryReaderTests : IDisposable
{
    private static readonly long s_day = HistoryTestSupport.Day;

    private readonly TempHistoryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Next_WhenTailIsTorn_StopsAtLastCompleteRecord()
    {
        using (HistoryWriter writer = new(_directory.FullPath))
        {
            writer.Begin(s_day);
            writer.Write("A.Value", 1);
            writer.End();
            writer.Begin(s_day + 1000);
            writer.Write("A.Value", 2);
            writer.End();
        }

        using (FileStream file = new(_directory.DayFile(s_day), FileMode.Open))
        {
            file.SetLength(file.Length - 3);
        }

        int samples = 0;
        using HistoryReader reader = new(_directory.DayFile(s_day));
        while (reader.Next())
        {
            samples += reader.Kind == HistoryFormat.SampleRecord ? 1 : 0;
        }

        Assert.Equal(1, samples);
    }
}
