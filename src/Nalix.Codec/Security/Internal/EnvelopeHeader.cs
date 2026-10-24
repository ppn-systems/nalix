// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
//
// Represents the fixed AEAD envelope header and provides span-first
// serialize / parse helpers.
//
// Header layout (12 bytes):
// [0..3]  : MAGIC "NALX" (4 ASCII bytes)
// [4]     : version (1 byte)
// [5]     : type  (1 byte)  -> CipherSuiteType (Nalix.Abstractions.Enums)
// [6]     : flags (1 byte)  (reserved)
// [7]     : nonceLen (1 byte)
// [8..11] : seq (uint32 little-endian)

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nalix.Abstractions.Security;

namespace Nalix.Codec.Security.Internal;

[System.Diagnostics.DebuggerNonUserCode]
[StructLayout(LayoutKind.Explicit, Size = 12)]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
internal readonly struct EnvelopeHeader
{
    #region Constants

    public const int SIZE = 12;
    internal const uint MAGIC_CONST = 0x584C414E;

    #endregion Constants

    #region Fields

    [FieldOffset(0)] public readonly uint MAGIC;
    [FieldOffset(4)] public readonly byte VERSION;
    [FieldOffset(5)] public readonly CipherSuiteType TYPE;
    [FieldOffset(6)] public readonly byte FLAGS;
    [FieldOffset(7)] public readonly byte NONCE_LEN;
    [FieldOffset(8)] public readonly uint SEQ;

    #endregion Fields

    #region Constructors

    internal EnvelopeHeader(
        [System.Diagnostics.CodeAnalysis.NotNull] byte version,
        [System.Diagnostics.CodeAnalysis.NotNull] CipherSuiteType type,
        [System.Diagnostics.CodeAnalysis.NotNull] byte flags,
        [System.Diagnostics.CodeAnalysis.NotNull] byte nonceLen,
        [System.Diagnostics.CodeAnalysis.NotNull] uint seq)
    {
        MAGIC = MAGIC_CONST;
        VERSION = version;
        TYPE = type;
        FLAGS = flags;
        NONCE_LEN = nonceLen;
        SEQ = seq;
    }

    #endregion Constructors

    #region Methods

    /// <summary>
    /// Writes header into dest (must be at least Size).
    /// </summary>
    /// <param name="dest"></param>
    /// <param name="header"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        MethodImplOptions.AggressiveOptimization)]
    internal static void Encode(System.Span<byte> dest, in EnvelopeHeader header)
    {
        // 1. Check the border only once here
        if ((uint)dest.Length < SIZE)
        {
            throw new System.ArgumentException("Destination too small for AEAD header.");
        }

        // 2. Write Magic "NALX" (4 bytes)
        // Use Unsafe.WriteUnaligned to write 4 bytes at once instead of CopyTo (more optimal for small constants)
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(dest), MAGIC_CONST);

        // 3. Pack 4 bytes (VERSION, TYPE, FLAGS, NONCE_LEN) into 1 uint to write
        // Take the address of VERSION and treat it as a uint (since these 4 fields are adjacent in the struct)
        // Note: Ensure the layout order in the EnvelopeHeader struct is sequential.
        uint metadata = header.VERSION | ((uint)header.TYPE << 8) | ((uint)header.FLAGS << 16) | ((uint)header.NONCE_LEN << 24);

        // Write these 4 bytes into dest[4..7] with a single CPU instruction
        Unsafe.WriteUnaligned(ref dest[4], metadata);

        // 4. Write SEQ (last 4 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(8, 4), header.SEQ);
    }

    /// <summary>
    /// Try parse header from src. Returns false if malformed/too short.
    /// </summary>
    /// <param name="src"></param>
    /// <param name="header"></param>
    [System.Runtime.CompilerServices.MethodImpl(
        MethodImplOptions.AggressiveOptimization)]
    [return: System.Diagnostics.CodeAnalysis.NotNull]
    internal static bool Decode(System.ReadOnlySpan<byte> src, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EnvelopeHeader header)
    {
        // 1. Check the boundary only once using the uint cast technique
        if ((uint)src.Length < SIZE)
        {
            header = default;
            return false;
        }

        // 2. Check the Magic "NALX" by comparing a single uint number (without using SequenceEqual)
        if (Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(src)) != MAGIC_CONST)
        {
            header = default;
            return false;
        }

        // 3. Read a 4-byte metadata block (Version, Type, Flags, NonceLen) simultaneously.
        uint metadata = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(src), 4));

        byte version = (byte)metadata;
        byte typeByte = (byte)(metadata >> 8);
        byte flags = (byte)(metadata >> 16);
        byte nonceLen = (byte)(metadata >> 24);

        // 4. Quick logic check
        if (version != EnvelopeFormat.CurrentVersion || !CipherSuiteMetadata.IsDefined(typeByte))
        {
            header = default;
            return false;
        }

        // 5. Read SEQ (last 4 bytes)
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8, 4));

        // Casting typeByte to Enum is free
        header = new EnvelopeHeader(version, Unsafe.As<byte, CipherSuiteType>(ref typeByte), flags, nonceLen, seq);

        return true;
    }

    #endregion Methods

    #region Cache

    private static class CipherSuiteMetadata
    {
        private static readonly bool[] s_types = BuildValidMap();

        private static bool[] BuildValidMap()
        {
            bool[] map = new bool[256]; // Byte range
            CipherSuiteType[] enumValues = Enum.GetValues<CipherSuiteType>();
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(enumValues.AsSpan());

            foreach (byte v in bytes)
            {
                map[v] = true;
            }

            return map;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsDefined(byte type) => s_types[type];
    }

    #endregion Cache
}
