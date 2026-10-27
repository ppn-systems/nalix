// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.IO;
using Nalix.Observability.Contracts;

namespace Nalix.Observability;

/// <summary>
/// Configures <see cref="ObservabilityApplicationBuilderExtensions.UseObservability(Nalix.Hosting.INetworkApplicationBuilder, Action{ObservabilityOptions})"/>.
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>Gets or sets the administrative key file. Defaults to <see cref="ObservabilityKey.DefaultPath"/>.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether numeric metrics are recorded to disk so a dashboard can browse past days.
    /// </summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>Gets or sets the history directory. Defaults to <c>observability-history</c> under the Nalix data directory.</summary>
    public string HistoryDirectory { get; set; } = Path.Combine(Directories.DataDirectory, "observability-history");

    /// <summary>Gets or sets the recording interval (minimum one second).</summary>
    public TimeSpan HistoryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets how many days of history files are kept; older files are deleted.</summary>
    public int HistoryRetentionDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets the subsystems recorded. Only numeric fields of objects are kept; per-item arrays
    /// (workers, middleware lists, …) are skipped because their cardinality is unbounded.
    /// </summary>
    public RuntimeObservationTargets HistoryTargets { get; set; } =
        RuntimeObservationTargets.PROCESS | RuntimeObservationTargets.CONNECTIONS |
        RuntimeObservationTargets.DISPATCH | RuntimeObservationTargets.SESSIONS;
}
