// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;

namespace Nalix.Observability.Internal.History;

/// <summary>
/// The on-disk history format: one append-only file per UTC day, <c>yyyyMMdd.nxh</c>.
/// </summary>
/// <remarks>
/// <code>
/// file    := magic "NXH1" record*
/// record  := COLUMN | SAMPLE                      (little-endian)
/// COLUMN  := 0x01 id:u16 len:u8 name:utf8[len]    defines a metric name for this file
/// SAMPLE  := 0x02 time:i64 count:u16 (id:u16 value:f32)[count]
/// </code>
/// A sample stores only the metrics whose value changed since the previous sample of the same file, so a
/// reader carries values forward. Every file starts from an empty state, which makes each day independently
/// readable. A torn record at the tail (crash mid-write) is ignored by readers.
/// </remarks>
internal static class HistoryFormat
{
    public const byte ColumnRecord = 0x01;
    public const byte SampleRecord = 0x02;
    public const int MaxColumns = 4096;
    public const int MaxNameBytes = 255;
    public const int PairSize = sizeof(ushort) + sizeof(float);
    public const int SampleHeaderSize = 1 + sizeof(long) + sizeof(ushort);
    public const string Extension = ".nxh";

    public static ReadOnlySpan<byte> Magic => "NXH1"u8;

    public static string FileName(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + Extension;

    public static bool TryParseDay(string path, out DateOnly day)
        => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    public static DateOnly DayOf(long unixMs) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);

    public static long StartOf(DateOnly day) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
}
