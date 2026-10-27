// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Environment.Memory;
using Nalix.Framework.Injection;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Observability.Contracts;

namespace Nalix.Observability.Internal.History;

/// <summary>
/// Samples the configured subsystems on a recurring job and appends their numeric fields to the day file.
/// </summary>
/// <remarks>
/// Per tick: one <see cref="ObservationWriter.Write"/> into a pooled buffer, one forward-only
/// <see cref="Utf8JsonReader"/> pass that builds metric paths in a reused char buffer (no strings for known
/// metrics), and one file write. Nothing is retained in memory beyond the column table and the last values.
/// </remarks>
internal sealed class HistoryRecorder : IDisposable
{
    private const string RecurringName = "observability.history";

    private readonly HistoryWriter _writer;
    private readonly RuntimeObservationTargets _targets;
    private readonly int _retentionDays;
    private char[] _path = new char[256];
    private IDisposable? _job;
    private DateOnly _lastRetentionDay;
    private double _cpuMs = double.NaN;
    private double _cores = double.NaN;
    private double _allocated = double.NaN;
    private double _collections;
    private double _prevCpuMs = double.NaN;
    private double _prevAllocated = double.NaN;
    private double _prevCollections = double.NaN;
    private long _prevTime;

    internal HistoryRecorder(ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory = options.HistoryDirectory;
        Interval = options.HistoryInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : options.HistoryInterval;
        _targets = options.HistoryTargets;
        _retentionDays = Math.Max(1, options.HistoryRetentionDays);
        _writer = new HistoryWriter(Directory);
    }

    /// <summary>Gets the recorder started by <c>UseObservability</c>, or <see langword="null"/> when history is off.</summary>
    public static HistoryRecorder? Current { get; private set; }

    /// <summary>Gets the directory holding the day files.</summary>
    public string Directory { get; }

    /// <summary>Gets the recording interval.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Gets the number of days kept.</summary>
    public int RetentionDays => _retentionDays;

    /// <summary>Creates a recorder, schedules its recurring job and makes it <see cref="Current"/> (replacing any previous one).</summary>
    public static void Start(ObservabilityOptions options)
    {
        Current?.Dispose();
        Current = new HistoryRecorder(options);
        Current.SCHEDULE();
    }

    private void SCHEDULE()
    {
        _job = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleRecurring(
            name: RecurringName,
            interval: Interval,
            work: _ =>
            {
                Sample(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return ValueTask.CompletedTask;
            },
            options: new RecurringOptions
            {
                NonReentrant = true,
                Tag = TaskNaming.Tags.Service,
                BackoffCap = TimeSpan.FromMinutes(1),
                ExecutionTimeout = TimeSpan.FromSeconds(5),
            });
    }

    /// <summary>Takes one sample at <paramref name="unixMs"/> and appends it.</summary>
    public void Sample(long unixMs)
    {
        PRUNE(HistoryFormat.DayOf(unixMs));

        using BufferLease payload = ObservationWriter.Write(_targets, out _);
        _writer.Begin(unixMs);
        _cpuMs = _cores = _allocated = double.NaN;
        _collections = 0;

        FLATTEN(payload.Span);
        DERIVE(unixMs);
        _writer.End();
    }

    public void Dispose()
    {
        _job?.Dispose();
        _job = null;
        _writer.Dispose();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }

    // Walks the JSON once; numeric fields of objects become "Section.Child.Field". Arrays are skipped.
    private void FLATTEN(ReadOnlySpan<byte> json)
    {
        Utf8JsonReader reader = new(json, new JsonReaderOptions { MaxDepth = 64 });
        Span<int> stack = stackalloc int[64];
        int depth = 0;
        int length = 0;
        int nameStart = 0;
        int nameLength = 0;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    nameStart = length == 0 ? 0 : length + 1;
                    nameLength = APPEND_NAME(ref reader, nameStart, length);
                    break;

                case JsonTokenType.StartObject:
                    if (depth < stack.Length)
                    {
                        stack[depth++] = length;
                    }

                    // The root object has no name; nested objects extend the path with their property name.
                    if (depth > 1)
                    {
                        length = nameStart + nameLength;
                    }

                    break;

                case JsonTokenType.EndObject:
                    if (depth > 0)
                    {
                        length = stack[--depth];
                    }

                    break;

                case JsonTokenType.StartArray:
                    reader.Skip();
                    break;

                case JsonTokenType.Number:
                    if (reader.TryGetDouble(out double value))
                    {
                        RECORD(_path.AsSpan(0, nameStart + nameLength), value);
                    }

                    break;

                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.String:
                case JsonTokenType.Null:
                case JsonTokenType.None:
                case JsonTokenType.EndArray:
                case JsonTokenType.Comment:
                default:
                    break;
            }
        }
    }

    // Writes "<prefix>.<name>" (or "<name>" at the root) into the path buffer and returns the name length.
    private int APPEND_NAME(ref Utf8JsonReader reader, int nameStart, int prefixLength)
    {
        int max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        if (_path.Length < nameStart + max)
        {
            Array.Resize(ref _path, Math.Max(_path.Length * 2, nameStart + max));
        }

        if (prefixLength > 0)
        {
            _path[prefixLength] = '.';
        }

        return reader.CopyString(_path.AsSpan(nameStart));
    }

    private void RECORD(ReadOnlySpan<char> path, double value)
    {
        _writer.Write(path, value);

        if (path.SequenceEqual("Process.CpuTimeMs"))
        {
            _cpuMs = value;
        }
        else if (path.SequenceEqual("Process.ProcessorCount"))
        {
            _cores = value;
        }
        else if (path.SequenceEqual("Process.TotalAllocatedBytes"))
        {
            _allocated = value;
        }
        else if (path.StartsWith("Process.Gen") && path.EndsWith("Collections"))
        {
            _collections += value;
        }
    }

    // Rates derived from cumulative counters, so history charts show CPU % rather than CPU time.
    private void DERIVE(long unixMs)
    {
        double elapsed = unixMs - _prevTime;
        bool hasPrevious = _prevTime > 0 && elapsed > 0;

        if (hasPrevious && !double.IsNaN(_cpuMs) && _cores > 0 && _cpuMs >= _prevCpuMs)
        {
            _writer.Write("Process.CpuPercent", Math.Clamp((_cpuMs - _prevCpuMs) / (elapsed * _cores) * 100, 0, 100));
        }

        if (hasPrevious && !double.IsNaN(_allocated) && _allocated >= _prevAllocated)
        {
            _writer.Write("Process.AllocationBytesPerSecond", (_allocated - _prevAllocated) * 1000 / elapsed);
        }

        if (hasPrevious && _collections >= _prevCollections)
        {
            _writer.Write("Process.GcPerSecond", (_collections - _prevCollections) * 1000 / elapsed);
        }

        _prevCpuMs = _cpuMs;
        _prevAllocated = _allocated;
        _prevCollections = _collections;
        _prevTime = unixMs;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Retention is best-effort; a file that cannot be deleted is retried the next day.")]
    private void PRUNE(DateOnly today)
    {
        if (today == _lastRetentionDay)
        {
            return;
        }

        _lastRetentionDay = today;
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        DateOnly oldest = today.AddDays(-(_retentionDays - 1));
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*" + HistoryFormat.Extension))
        {
            if (HistoryFormat.TryParseDay(file, out DateOnly day) && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Retried on the next day change.
                }
            }
        }
    }
}
