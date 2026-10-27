// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Text;

namespace Nalix.Observability.Internal.History;

/// <summary>
/// Appends samples to the day file, writing only values that changed since the previous sample.
/// </summary>
/// <remarks>
/// Not thread-safe; the recorder calls it from a single non-reentrant recurring job. Column lookups go through a
/// span-keyed alternate lookup, so known metrics cost no allocation per sample.
/// </remarks>
internal sealed class HistoryWriter : IDisposable
{
    private readonly string _directory;
    private readonly Dictionary<string, ushort> _columns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ushort>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private float[] _last = new float[64];
    private byte[] _buffer = new byte[4096];
    private readonly List<(ushort Id, float Value)> _pairs = [];
    private int _length;
    private long _sampleTime;
    private FileStream? _file;
    private DateOnly _day;

    public HistoryWriter(string directory)
    {
        _directory = directory;
        _lookup = _columns.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Gets the size of the current day file in bytes.</summary>
    public long FileLength => _file?.Length ?? 0;

    /// <summary>Starts a sample at <paramref name="unixMs"/>, rolling to a new day file when the UTC date changed.</summary>
    public void Begin(long unixMs)
    {
        DateOnly day = HistoryFormat.DayOf(unixMs);
        if (_file is null || day != _day)
        {
            OPEN(day);
        }

        // Column definitions found during this sample are buffered first, so they precede the sample record.
        _length = 0;
        _pairs.Clear();
        _sampleTime = unixMs;
    }

    /// <summary>Records a value for this sample; skipped when it equals the previous value of the same metric.</summary>
    public void Write(ReadOnlySpan<char> name, double value)
    {
        float f = (float)value;
        if (!_lookup.TryGetValue(name, out ushort id))
        {
            if (_columns.Count >= HistoryFormat.MaxColumns || Encoding.UTF8.GetByteCount(name) > HistoryFormat.MaxNameBytes)
            {
                return;
            }

            id = (ushort)_columns.Count;
            string key = new(name);
            _columns.Add(key, id);
            APPEND_COLUMN(id, key);
            if (id >= _last.Length)
            {
                Array.Resize(ref _last, _last.Length * 2);
            }

            _last[id] = float.NaN;
        }

        if (BitConverter.SingleToInt32Bits(_last[id]) == BitConverter.SingleToInt32Bits(f))
        {
            return;
        }

        _last[id] = f;
        _pairs.Add((id, f));
    }

    /// <summary>Writes the buffered column definitions and sample record in one write call.</summary>
    public void End()
    {
        if (_pairs.Count > 0)
        {
            int count = Math.Min(_pairs.Count, ushort.MaxValue);
            ENSURE(HistoryFormat.SampleHeaderSize + (count * HistoryFormat.PairSize));
            Span<byte> span = _buffer.AsSpan(_length);
            span[0] = HistoryFormat.SampleRecord;
            BinaryPrimitives.WriteInt64LittleEndian(span[1..], _sampleTime);
            BinaryPrimitives.WriteUInt16LittleEndian(span[9..], (ushort)count);
            int offset = HistoryFormat.SampleHeaderSize;
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], _pairs[i].Id);
                BinaryPrimitives.WriteSingleLittleEndian(span[(offset + 2)..], _pairs[i].Value);
                offset += HistoryFormat.PairSize;
            }

            _length += offset;
        }

        _pairs.Clear();
        if (_length > 0 && _file is not null)
        {
            _file.Write(_buffer, 0, _length);
            _file.Flush();
        }

        _length = 0;
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
    }

    private void OPEN(DateOnly day)
    {
        _file?.Dispose();
        _ = Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, HistoryFormat.FileName(day));

        // A file left by an earlier run on the same day is continued: its column table is re-read so ids stay
        // consistent and the day remains one file.
        _columns.Clear();
        Array.Fill(_last, float.NaN);
        bool exists = File.Exists(path) && new FileInfo(path).Length >= HistoryFormat.Magic.Length;
        if (exists)
        {
            LOAD_COLUMNS(path);
        }

        _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, FileOptions.None);
        if (!exists)
        {
            _file.SetLength(0);
            _file.Write(HistoryFormat.Magic);
        }

        _day = day;
    }

    private void LOAD_COLUMNS(string path)
    {
        using HistoryReader reader = new(path);
        while (reader.Next())
        {
            if (reader.Kind == HistoryFormat.ColumnRecord && !_columns.ContainsKey(reader.ColumnName))
            {
                _columns.Add(reader.ColumnName, reader.ColumnId);
                if (reader.ColumnId >= _last.Length)
                {
                    Array.Resize(ref _last, Math.Max(_last.Length * 2, reader.ColumnId + 1));
                }
            }
        }

        // Values carried in the file are unknown here, so the first sample of this run rewrites every metric.
        Array.Fill(_last, float.NaN);
    }

    private void APPEND_COLUMN(ushort id, string name)
    {
        int byteCount = Encoding.UTF8.GetByteCount(name);
        ENSURE(1 + sizeof(ushort) + 1 + byteCount);
        Span<byte> span = _buffer.AsSpan(_length);
        span[0] = HistoryFormat.ColumnRecord;
        BinaryPrimitives.WriteUInt16LittleEndian(span[1..], id);
        span[3] = (byte)byteCount;
        _ = Encoding.UTF8.GetBytes(name, span[4..]);
        _length += 4 + byteCount;
    }

    private void ENSURE(int extra)
    {
        if (_length + extra > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + extra));
        }
    }
}
