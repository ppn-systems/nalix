// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Text.Json;
using Nalix.Observability.Internal.History;

namespace Nalix.Observability.Tests.TestSupport;

/// <summary>
/// Shared fixtures for the history tests: the reference day, metric naming, sample writing and JSON value extraction.
/// </summary>
internal static class HistoryTestSupport
{
    /// <summary>The UTC midnight, in Unix milliseconds, of the day every history test writes into.</summary>
    internal static readonly long Day = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>Returns the name of the synthetic metric with the given index.</summary>
    internal static string MetricName(int metric) => "Section.Metric" + metric.ToString(CultureInfo.InvariantCulture);

    /// <summary>Writes one sample holding the given name/value pairs at <paramref name="time"/>.</summary>
    internal static void WriteSample(HistoryWriter writer, long time, params (string Name, double Value)[] values)
    {
        writer.Begin(time);
        foreach ((string name, double value) in values)
        {
            writer.Write(name, value);
        }

        writer.End();
    }

    /// <summary>Reads a JSON array of numbers into a double array.</summary>
    internal static double[] Values(JsonElement array) => [.. array.EnumerateArray().Select(v => v.GetDouble())];
}
