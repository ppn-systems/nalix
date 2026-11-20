// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System;

namespace Nalix.Environment.Datagrams.Wire;

/// <summary>
/// QUIC variable-length integer (RFC 9000 §16): the two high bits of the first byte select a 1, 2, 4 or
/// 8-byte big-endian encoding of a value up to 2^62 - 1.
/// </summary>
internal static class VarInt
{
    public const ulong MaxValue = (1UL << 62) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SizeOf(ulong value) => value switch
    {
        < 1UL << 6 => 1,
        < 1UL << 14 => 2,
        < 1UL << 30 => 4,
        <= MaxValue => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Varint values are limited to 62 bits."),
    };

    /// <summary>Writes <paramref name="value"/>; returns the number of bytes written.</summary>
    public static int Write(Span<byte> destination, ulong value)
    {
        int size = SizeOf(value);
        switch (size)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(value | 0x4000));
                break;
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value | 0x8000_0000);
                break;
            default:
                BinaryPrimitives.WriteUInt64BigEndian(destination, value | 0xC000_0000_0000_0000);
                break;
        }

        return size;
    }

    /// <summary>Reads a varint; returns <see langword="false"/> when <paramref name="source"/> is too short.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out ulong value, out int size)
    {
        if (source.IsEmpty)
        {
            value = 0;
            size = 0;
            return false;
        }

        size = 1 << (source[0] >> 6);
        if (source.Length < size)
        {
            value = 0;
            return false;
        }

        value = size switch
        {
            1 => source[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(source) & 0x3FFFUL,
            4 => BinaryPrimitives.ReadUInt32BigEndian(source) & 0x3FFF_FFFFUL,
            _ => BinaryPrimitives.ReadUInt64BigEndian(source) & 0x3FFF_FFFF_FFFF_FFFFUL,
        };
        return true;
    }

    /// <summary>Reads a varint at <paramref name="offset"/> and advances it.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, ref int offset, out ulong value)
    {
        if (!TryRead(source[offset..], out value, out int size))
        {
            return false;
        }

        offset += size;
        return true;
    }
}
