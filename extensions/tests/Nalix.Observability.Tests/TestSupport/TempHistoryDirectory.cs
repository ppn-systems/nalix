// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Observability.Contracts;
using Nalix.Observability.Internal.History;

namespace Nalix.Observability.Tests.TestSupport;

/// <summary>
/// A unique history directory under the temp path, removed on dispose, with factories for recorders that use it.
/// </summary>
internal sealed class TempHistoryDirectory : IDisposable
{
    public TempHistoryDirectory()
    {
        FullPath = Path.Combine(Path.GetTempPath(), "nxh-" + Path.GetRandomFileName());
    }

    /// <summary>Gets the absolute path of the history directory. The directory is created on first write.</summary>
    public string FullPath { get; }

    /// <summary>Gets the day file that holds the samples of the UTC day containing <paramref name="unixMilliseconds"/>.</summary>
    public string DayFile(long unixMilliseconds) =>
        Path.Combine(FullPath, HistoryFormat.FileName(HistoryFormat.DayOf(unixMilliseconds)));

    /// <summary>Creates a recorder that samples the process targets into this directory.</summary>
    public HistoryRecorder CreateRecorder(int retentionDays = 7) => new(new ObservabilityOptions
    {
        HistoryDirectory = FullPath,
        HistoryInterval = TimeSpan.FromSeconds(10),
        HistoryRetentionDays = retentionDays,
        HistoryTargets = RuntimeObservationTargets.PROCESS,
    });

    public void Dispose()
    {
        if (Directory.Exists(FullPath))
        {
            Directory.Delete(FullPath, recursive: true);
        }
    }
}
