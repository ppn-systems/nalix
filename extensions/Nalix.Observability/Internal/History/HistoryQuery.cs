// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Environment.Memory;
using Nalix.Observability.Contracts;

namespace Nalix.Observability.Internal.History;

/// <summary>
/// Answers recorded-history queries straight from the day files.
/// </summary>
/// <remarks>
/// Files are streamed once, front to back, through <see cref="HistoryReader"/>; samples are folded into at most
/// <see cref="MaxPoints"/> buckets per metric as they are read. Memory is O(metrics × points), independent of
/// how much history is on disk, and nothing is cached between queries.
/// </remarks>
internal static class HistoryQuery
{
    public const int MaxPoints = 2000;
    public const int MaxMetrics = 32;
    private const int DefaultPoints = 600;

    private static readonly JsonWriterOptions s_options = new() { Indented = false, SkipValidation = true };

    /// <summary>Parses a query; returns <see langword="false"/> for malformed JSON or an empty/inverted range.</summary>
    public static bool TryParse(ReadOnlySpan<byte> json, out long from, out long to, out int points, out List<string> metrics)
    {
        from = 0;
        to = 0;
        points = DefaultPoints;
        metrics = [];

        try
        {
            Utf8JsonReader reader = new(json, new JsonReaderOptions { MaxDepth = 4 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals(RuntimeObservationHistory.From))
                {
                    _ = reader.Read();
                    from = reader.GetInt64();
                }
                else if (reader.ValueTextEquals(RuntimeObservationHistory.To))
                {
                    _ = reader.Read();
                    to = reader.GetInt64();
                }
                else if (reader.ValueTextEquals(RuntimeObservationHistory.Points))
                {
                    _ = reader.Read();
                    points = Math.Clamp(reader.GetInt32(), 1, MaxPoints);
                }
                else if (reader.ValueTextEquals(RuntimeObservationHistory.Metrics))
                {
                    _ = reader.Read();
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType == JsonTokenType.String)
                        {
                            if (metrics.Count < MaxMetrics)
                            {
                                metrics.Add(reader.GetString()!);
                            }
                        }
                    }
                }
                else
                {
                    _ = reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return to > from;
    }

    /// <summary>Writes the catalog: recorded days overlapping the range and every metric recorded in them.</summary>
    public static BufferLease Catalog(HistoryRecorder recorder, long from, long to)
    {
        using BufferWriter buffer = new(4 * 1024);
        using Utf8JsonWriter writer = new(buffer, s_options);
        SortedSet<string> metrics = new(StringComparer.Ordinal);

        writer.WriteStartObject();
        writer.WriteNumber(RuntimeObservationHistory.Interval, (long)recorder.Interval.TotalMilliseconds);
        writer.WriteStartArray(RuntimeObservationHistory.Days);
        foreach ((DateOnly day, string path) in DAYS(recorder.Directory, from, to))
        {
            long first = 0;
            long last = 0;
            int samples = 0;
            using (HistoryReader reader = new(path))
            {
                while (reader.Next())
                {
                    if (reader.Kind == HistoryFormat.ColumnRecord)
                    {
                        _ = metrics.Add(reader.ColumnName);
                    }
                    else
                    {
                        first = samples == 0 ? reader.SampleTime : first;
                        last = reader.SampleTime;
                        samples++;
                    }
                }
            }

            writer.WriteStartObject();
            writer.WriteString("date", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteNumber(RuntimeObservationHistory.From, first);
            writer.WriteNumber(RuntimeObservationHistory.To, last);
            writer.WriteNumber("samples", samples);
            writer.WriteNumber("bytes", new FileInfo(path).Length);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray(RuntimeObservationHistory.Metrics);
        foreach (string metric in metrics)
        {
            writer.WriteStringValue(metric);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ExtractLease();
    }

    /// <summary>Writes the requested metrics folded into buckets (average and maximum per bucket).</summary>
    public static BufferLease Series(HistoryRecorder recorder, long from, long to, int points, List<string> metrics)
    {
        int metricCount = metrics.Count;
        long step = Math.Max(1, (to - from + points - 1) / points);
        int buckets = (int)Math.Min(points, ((to - from) / step) + 1);

        double[] sum = new double[metricCount * buckets];
        float[] max = new float[metricCount * buckets];
        int[] count = new int[metricCount * buckets];
        float[] current = new float[metricCount];
        Dictionary<string, int> wanted = new(metricCount, StringComparer.Ordinal);
        for (int m = 0; m < metricCount; m++)
        {
            wanted[metrics[m]] = m;
        }

        int[] map = new int[HistoryFormat.MaxColumns];
        foreach ((_, string path) in DAYS(recorder.Directory, from, to))
        {
            // Column ids and carried values are per file: every day starts from an empty state.
            Array.Fill(map, -1);
            Array.Fill(current, float.NaN);

            using HistoryReader reader = new(path);
            while (reader.Next())
            {
                if (reader.Kind == HistoryFormat.ColumnRecord)
                {
                    if (wanted.TryGetValue(reader.ColumnName, out int metric) && reader.ColumnId < map.Length)
                    {
                        map[reader.ColumnId] = metric;
                    }

                    continue;
                }

                for (int i = 0; i < reader.PairCount; i++)
                {
                    (ushort id, float value) = reader.GetPair(i);
                    int metric = id < map.Length ? map[id] : -1;
                    if (metric >= 0)
                    {
                        current[metric] = value;
                    }
                }

                long time = reader.SampleTime;
                if (time < from || time > to)
                {
                    continue;
                }

                int bucket = (int)Math.Min(buckets - 1, (time - from) / step);
                for (int m = 0; m < metricCount; m++)
                {
                    float value = current[m];
                    if (float.IsNaN(value))
                    {
                        continue;
                    }

                    int index = (m * buckets) + bucket;
                    sum[index] += value;
                    max[index] = count[index] == 0 ? value : Math.Max(max[index], value);
                    count[index]++;
                }
            }
        }

        using BufferWriter buffer = new(Math.Max(1024, metricCount * buckets * 24));
        using Utf8JsonWriter writer = new(buffer, s_options);
        writer.WriteStartObject();
        writer.WriteNumber(RuntimeObservationHistory.From, from);
        writer.WriteNumber(RuntimeObservationHistory.To, to);
        writer.WriteNumber(RuntimeObservationHistory.Step, step);
        writer.WriteStartArray(RuntimeObservationHistory.Times);
        for (int b = 0; b < buckets; b++)
        {
            writer.WriteNumberValue(from + (b * step));
        }

        writer.WriteEndArray();
        writer.WriteStartObject(RuntimeObservationHistory.Series);
        for (int m = 0; m < metricCount; m++)
        {
            writer.WriteStartObject(metrics[m]);
            writer.WriteStartArray(RuntimeObservationHistory.Average);
            for (int b = 0; b < buckets; b++)
            {
                int index = (m * buckets) + b;
                if (count[index] == 0)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteNumberValue((float)(sum[index] / count[index]));
                }
            }

            writer.WriteEndArray();
            writer.WriteStartArray(RuntimeObservationHistory.Maximum);
            for (int b = 0; b < buckets; b++)
            {
                int index = (m * buckets) + b;
                if (count[index] == 0)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteNumberValue(max[index]);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ExtractLease();
    }

    // Day files whose UTC date overlaps [from, to], oldest first. Enumerates what exists rather than walking
    // the calendar, so an unbounded range costs one directory listing.
    private static List<(DateOnly Day, string Path)> DAYS(string directory, long from, long to)
    {
        List<(DateOnly Day, string Path)> days = [];
        if (!Directory.Exists(directory))
        {
            return days;
        }

        DateOnly first = HistoryFormat.DayOf(Math.Max(from, 0));
        DateOnly last = HistoryFormat.DayOf(Math.Max(to, 0));
        foreach (string path in Directory.EnumerateFiles(directory, "*" + HistoryFormat.Extension))
        {
            if (HistoryFormat.TryParseDay(path, out DateOnly day) && day >= first && day <= last)
            {
                days.Add((day, path));
            }
        }

        days.Sort(static (a, b) => a.Day.CompareTo(b.Day));
        return days;
    }
}
