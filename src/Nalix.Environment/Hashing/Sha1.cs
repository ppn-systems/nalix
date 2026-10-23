// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nalix.Environment.Hashing;

/// <summary>
/// Provides a high-performance, zero-allocation managed implementation of the RFC 3174 (FIPS 180-4) SHA-1 hashing algorithm.
/// </summary>
/// <remarks>
/// <para>
/// Uses an unaligned big-endian load path (<see cref="Unsafe.ReadUnaligned{T}(ref readonly byte)"/> +
/// <see cref="BinaryPrimitives.ReverseEndianness(uint)"/>) and a 16-word circular message schedule
/// split into 4 branchless 20-round stages to keep state in CPU registers / L1 cache with zero heap allocation.
/// </para>
/// <para>
/// Intended for protocol-level framing requirements that mandate SHA-1 by specification
/// (such as RFC 6455 §4.2.2 <c>Sec-WebSocket-Accept</c> handshake derivation).
/// Do not use SHA-1 for password hashing or digital signatures.
/// </para>
/// </remarks>
[SkipLocalsInit]
public static class Sha1
{
    /// <summary>
    /// The size, in bytes, of a SHA-1 digest (160 bits).
    /// </summary>
    public const int HashSize = 20;

    private const int BlockSize = 64;

    /// <summary>
    /// Computes the SHA-1 digest of <paramref name="data"/> and returns a newly allocated 20-byte array.
    /// </summary>
    /// <param name="data">The input data to hash.</param>
    /// <returns>A 20-byte array containing the SHA-1 digest.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        byte[] result = GC.AllocateUninitializedArray<byte>(HashSize);
        HashData(data, result);
        return result;
    }

    /// <summary>
    /// Attempts to compute the SHA-1 digest of <paramref name="data"/> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="data">The input data to hash.</param>
    /// <param name="destination">The buffer to receive the 20-byte digest.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="destination"/> is at least <see cref="HashSize"/> bytes long
    /// and the hash was written; otherwise, <see langword="false"/>.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool TryHashData(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (destination.Length < HashSize)
        {
            return false;
        }

        HashData(data, destination);
        return true;
    }

    /// <summary>
    /// Computes the SHA-1 digest of <paramref name="data"/> and writes the 20-byte result into <paramref name="destination"/>.
    /// </summary>
    /// <param name="data">The input data to hash.</param>
    /// <param name="destination">The destination span (must be at least <see cref="HashSize"/> bytes).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="destination"/> is shorter than <see cref="HashSize"/> bytes.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void HashData(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (destination.Length < HashSize)
        {
            throw new ArgumentException(
                $"Destination buffer must be at least {HashSize} bytes.", nameof(destination));
        }

        uint h0 = 0x67452301u;
        uint h1 = 0xEFCDAB89u;
        uint h2 = 0x98BADCFEu;
        uint h3 = 0x10325476u;
        uint h4 = 0xC3D2E1F0u;

        Span<uint> w = stackalloc uint[16];
        ref uint wRef = ref MemoryMarshal.GetReference(w);
        ref byte dataRef = ref MemoryMarshal.GetReference(data);

        int length = data.Length;
        int offset = 0;

        while (length - offset >= BlockSize)
        {
            ProcessBlock(ref h0, ref h1, ref h2, ref h3, ref h4, ref Unsafe.Add(ref dataRef, offset), ref wRef);
            offset += BlockSize;
        }

        int remaining = length - offset;
        Span<byte> lastBlock = stackalloc byte[BlockSize];
        lastBlock.Clear();

        if (remaining > 0)
        {
            Unsafe.CopyBlockUnaligned(
                ref MemoryMarshal.GetReference(lastBlock),
                ref Unsafe.Add(ref dataRef, offset),
                (uint)remaining);
        }

        ref byte lastBlockRef = ref MemoryMarshal.GetReference(lastBlock);
        Unsafe.Add(ref lastBlockRef, remaining) = 0x80;

        if (remaining >= 56)
        {
            ProcessBlock(ref h0, ref h1, ref h2, ref h3, ref h4, ref lastBlockRef, ref wRef);
            lastBlock.Clear();
        }

        ulong bitLength = (ulong)length * 8UL;
        BinaryPrimitives.WriteUInt64BigEndian(lastBlock[56..BlockSize], bitLength);
        ProcessBlock(ref h0, ref h1, ref h2, ref h3, ref h4, ref lastBlockRef, ref wRef);

        ref byte dstRef = ref MemoryMarshal.GetReference(destination);
        WriteUInt32BigEndianUnaligned(ref dstRef, 0, h0);
        WriteUInt32BigEndianUnaligned(ref dstRef, 4, h1);
        WriteUInt32BigEndianUnaligned(ref dstRef, 8, h2);
        WriteUInt32BigEndianUnaligned(ref dstRef, 12, h3);
        WriteUInt32BigEndianUnaligned(ref dstRef, 16, h4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static void ProcessBlock(
        ref uint h0,
        ref uint h1,
        ref uint h2,
        ref uint h3,
        ref uint h4,
        ref byte blockRef,
        ref uint wRef)
    {
        for (int t = 0; t < 16; t++)
        {
            Unsafe.Add(ref wRef, t) = ReadUInt32BigEndianUnaligned(ref blockRef, t * 4);
        }

        uint a = h0;
        uint b = h1;
        uint c = h2;
        uint d = h3;
        uint e = h4;

        // Rounds 0..15 (schedule words already loaded in w[0..15])
        for (int t = 0; t < 16; t++)
        {
            uint f = (b & c) | (~b & d);
            uint temp = BitOperations.RotateLeft(a, 5) + f + e + 0x5A827999u + Unsafe.Add(ref wRef, t);
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = temp;
        }

        // Rounds 16..19 (circular 16-word schedule + Ch function)
        for (int t = 16; t < 20; t++)
        {
            uint wt = ExpandWord(ref wRef, t);
            uint f = (b & c) | (~b & d);
            uint temp = BitOperations.RotateLeft(a, 5) + f + e + 0x5A827999u + wt;
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = temp;
        }

        // Rounds 20..39 (circular 16-word schedule + Parity function)
        for (int t = 20; t < 40; t++)
        {
            uint wt = ExpandWord(ref wRef, t);
            uint f = b ^ c ^ d;
            uint temp = BitOperations.RotateLeft(a, 5) + f + e + 0x6ED9EBA1u + wt;
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = temp;
        }

        // Rounds 40..59 (circular 16-word schedule + Maj function)
        for (int t = 40; t < 60; t++)
        {
            uint wt = ExpandWord(ref wRef, t);
            uint f = (b & c) | (b & d) | (c & d);
            uint temp = BitOperations.RotateLeft(a, 5) + f + e + 0x8F1BBCDCu + wt;
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = temp;
        }

        // Rounds 60..79 (circular 16-word schedule + Parity function)
        for (int t = 60; t < 80; t++)
        {
            uint wt = ExpandWord(ref wRef, t);
            uint f = b ^ c ^ d;
            uint temp = BitOperations.RotateLeft(a, 5) + f + e + 0xCA62C1D6u + wt;
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = temp;
        }

        h0 += a;
        h1 += b;
        h2 += c;
        h3 += d;
        h4 += e;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ExpandWord(ref uint wRef, int t)
    {
        int s = t & 15;
        uint wt = BitOperations.RotateLeft(
            Unsafe.Add(ref wRef, s) ^
            Unsafe.Add(ref wRef, (s + 13) & 15) ^
            Unsafe.Add(ref wRef, (s + 8) & 15) ^
            Unsafe.Add(ref wRef, (s + 2) & 15),
            1);
        Unsafe.Add(ref wRef, s) = wt;
        return wt;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt32BigEndianUnaligned(ref byte source, int byteOffset)
    {
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, byteOffset));
        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt32BigEndianUnaligned(ref byte destination, int byteOffset, uint value)
    {
        uint bigEndian = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, byteOffset), bigEndian);
    }
}
