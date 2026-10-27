// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Nalix.Observability.Internal.History;

/// <summary>
/// Streams the records of one history file through a pooled 64 KiB buffer.
/// </summary>
/// <remarks>
/// Memory stays constant regardless of file size. The file is opened with <see cref="FileShare.ReadWrite"/> so it
/// can be read while the recorder appends; a torn record at the tail simply ends the iteration.
/// </remarks>
internal sealed class HistoryReader : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly FileStream _stream;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _start;
    private int _end;
    private int _pairsOffset;
    private bool _eof;

    public HistoryReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        IsValid = ENSURE(HistoryFormat.Magic.Length) && _buffer.AsSpan(_start, HistoryFormat.Magic.Length).SequenceEqual(HistoryFormat.Magic);
        _start += HistoryFormat.Magic.Length;
    }

    /// <summary>Gets a value indicating whether the file starts with the expected magic.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the kind of the current record.</summary>
    public byte Kind { get; private set; }

    /// <summary>Gets the id of the current column record.</summary>
    public ushort ColumnId { get; private set; }

    /// <summary>Gets the name of the current column record.</summary>
    public string ColumnName { get; private set; } = string.Empty;

    /// <summary>Gets the time of the current sample record, Unix milliseconds.</summary>
    public long SampleTime { get; private set; }

    /// <summary>Gets the number of (id, value) pairs in the current sample record.</summary>
    public int PairCount { get; private set; }

    /// <summary>Reads pair <paramref name="index"/> of the current sample record.</summary>
    public (ushort Id, float Value) GetPair(int index)
    {
        ReadOnlySpan<byte> span = _buffer.AsSpan(_pairsOffset + (index * HistoryFormat.PairSize), HistoryFormat.PairSize);
        return (BinaryPrimitives.ReadUInt16LittleEndian(span), BinaryPrimitives.ReadSingleLittleEndian(span[2..]));
    }

    /// <summary>Advances to the next complete record.</summary>
    /// <returns><see langword="false"/> at the end of the file, at a torn tail, or on an unknown record.</returns>
    public bool Next()
    {
        if (!IsValid || !ENSURE(1))
        {
            return false;
        }

        byte kind = _buffer[_start];
        switch (kind)
        {
            case HistoryFormat.ColumnRecord:
            {
                if (!ENSURE(4))
                {
                    return false;
                }

                int length = _buffer[_start + 3];
                if (!ENSURE(4 + length))
                {
                    return false;
                }

                ColumnId = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_start + 1));
                ColumnName = Encoding.UTF8.GetString(_buffer, _start + 4, length);
                _start += 4 + length;
                break;
            }

            case HistoryFormat.SampleRecord:
            {
                if (!ENSURE(HistoryFormat.SampleHeaderSize))
                {
                    return false;
                }

                int count = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_start + 9));
                int size = HistoryFormat.SampleHeaderSize + (count * HistoryFormat.PairSize);
                if (!ENSURE(size))
                {
                    return false;
                }

                SampleTime = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_start + 1));
                PairCount = count;
                _pairsOffset = _start + HistoryFormat.SampleHeaderSize;
                _start += size;
                break;
            }

            default:
                return false;
        }

        Kind = kind;
        return true;
    }

    public void Dispose()
    {
        _stream.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    // Makes at least `count` unread bytes available from _start, compacting and refilling the buffer.
    private bool ENSURE(int count)
    {
        while (_end - _start < count)
        {
            if (_eof)
            {
                return false;
            }

            if (count > _buffer.Length)
            {
                byte[] larger = ArrayPool<byte>.Shared.Rent(count);
                _buffer.AsSpan(_start, _end - _start).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = larger;
                _end -= _start;
                _start = 0;
            }
            else if (_start > 0)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start;
                _start = 0;
            }

            int read = _stream.Read(_buffer, _end, _buffer.Length - _end);
            if (read == 0)
            {
                _eof = true;
            }

            _end += read;
        }

        return true;
    }
}
