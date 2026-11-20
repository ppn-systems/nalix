// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System;

namespace Nalix.Environment.Datagrams.Wire;

/// <summary>
/// The 13-byte cleartext NDP header, authenticated as AAD:
/// <c>u8 Flags (Version 7..4 | KeyPhase 3 | reserved 2..0) · u64 LE ConnectionId · u32 LE PacketNumber (low 32 bits)</c>.
/// </summary>
internal readonly struct DatagramHeader
{
    public const int Size = 13;
    public const byte Version = 1;

    private const byte KeyPhaseBit = 0b0000_1000;
    private const byte ReservedBits = 0b0000_0111;

    public DatagramHeader(bool keyPhase, ulong connectionId, uint truncatedPacketNumber)
    {
        this.KeyPhase = keyPhase;
        this.ConnectionId = connectionId;
        this.TruncatedPacketNumber = truncatedPacketNumber;
    }

    public bool KeyPhase { get; }

    public ulong ConnectionId { get; }

    public uint TruncatedPacketNumber { get; }

    public void WriteTo(Span<byte> destination)
    {
        destination[0] = (byte)((Version << 4) | (this.KeyPhase ? KeyPhaseBit : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..], this.ConnectionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[9..], this.TruncatedPacketNumber);
    }

    /// <summary>
    /// Parses a header. Fails on a short buffer, an unknown version or non-zero reserved bits.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out DatagramHeader header)
    {
        header = default;
        if (source.Length < Size)
        {
            return false;
        }

        byte flags = source[0];
        if ((flags >> 4) != Version || (flags & ReservedBits) != 0)
        {
            return false;
        }

        header = new DatagramHeader(
            (flags & KeyPhaseBit) != 0,
            BinaryPrimitives.ReadUInt64LittleEndian(source[1..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[9..]));
        return true;
    }

    /// <summary>
    /// Reads only the connection identifier, for routing a datagram to its session.
    /// </summary>
    public static bool TryReadConnectionId(ReadOnlySpan<byte> source, out ulong connectionId)
    {
        if (source.Length < Size)
        {
            connectionId = 0;
            return false;
        }

        connectionId = BinaryPrimitives.ReadUInt64LittleEndian(source[1..]);
        return true;
    }

    /// <summary>
    /// Reconstructs the full packet number from its low 32 bits (RFC 9000 §A.3).
    /// </summary>
    /// <param name="truncated">The low 32 bits from the header.</param>
    /// <param name="largestReceived">The largest packet number received so far, or -1 when none.</param>
    public static ulong DecodePacketNumber(uint truncated, long largestReceived)
    {
        const ulong window = 1UL << 32;
        const ulong halfWindow = window / 2;
        const ulong mask = window - 1;

        ulong expected = (ulong)(largestReceived + 1);
        ulong candidate = (expected & ~mask) | truncated;

        if (candidate + halfWindow <= expected && candidate < (1UL << 62) - window)
        {
            return candidate + window;
        }

        if (candidate > expected + halfWindow && candidate >= window)
        {
            return candidate - window;
        }

        return candidate;
    }
}
