// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Extensions;

/// <summary>
/// Extension methods for reading primitive and common types from <see cref="DataReader"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DataReaderExtensions
{
    #region Primitive Types

    /// <summary>
    /// Reads a <see cref="byte"/> from the buffer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadByte(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(byte)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(byte));
        byte value = ptr;
        reader.Advance(sizeof(byte));
        return value;
    }

    /// <summary>
    /// Reads a <see cref="ushort"/> from the buffer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadUInt16(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(ushort)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(ushort));
        ushort value = Unsafe.ReadUnaligned<ushort>(ref ptr);
        reader.Advance(sizeof(ushort));
        return value;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ReadInt16(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(short)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(short));
        short value = Unsafe.ReadUnaligned<short>(ref ptr);
        reader.Advance(sizeof(short));
        return value;
    }

    /// <summary>
    /// Reads a <see cref="uint"/> from the buffer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(uint)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(uint));
        uint value = Unsafe.ReadUnaligned<uint>(ref ptr);
        reader.Advance(sizeof(uint));
        return value;
    }

    /// <summary>
    /// Reads a <see cref="int"/> from the buffer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInt32(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(int)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(int));
        int value = Unsafe.ReadUnaligned<int>(ref ptr);
        reader.Advance(sizeof(int));
        return value;
    }

    /// <summary>
    /// Reads a <see cref="long"/> from the buffer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadInt64(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(long)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(long));
        long value = Unsafe.ReadUnaligned<long>(ref ptr);
        reader.Advance(sizeof(long));
        return value;
    }

    /// <summary>
    /// Reads a <see cref="ulong"/> from the buffer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUInt64(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(ulong)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(ulong));
        ulong value = Unsafe.ReadUnaligned<ulong>(ref ptr);
        reader.Advance(sizeof(ulong));
        return value;
    }

    /// <summary>
    /// Reads a <see cref="bool"/> from the buffer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBoolean(this ref DataReader reader) => reader.ReadByte() != 0;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static sbyte ReadSByte(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(sbyte)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(sbyte));
        sbyte value = (sbyte)ptr;
        reader.Advance(sizeof(sbyte));
        return value;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char ReadChar(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(char)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(char));
        char value = Unsafe.ReadUnaligned<char>(ref ptr);
        reader.Advance(sizeof(char));
        return value;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReadSingle(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(float)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(float));
        float value = Unsafe.ReadUnaligned<float>(ref ptr);
        reader.Advance(sizeof(float));
        return value;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadDouble(this ref DataReader reader)
    {
        if (reader.BytesRemaining < sizeof(double)) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte ptr = ref reader.GetSpanReference(sizeof(double));
        double value = Unsafe.ReadUnaligned<double>(ref ptr);
        reader.Advance(sizeof(double));
        return value;
    }

    #endregion Primitive Types

    #region Enum Types

    /// <summary>
    /// Reads an enum value with underlying type <see cref="byte"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ReadEnumByte<TEnum>(this ref DataReader reader)
    where TEnum : unmanaged, Enum
    {
        byte value = reader.ReadByte();
        return Unsafe.As<byte, TEnum>(ref value);
    }

    /// <summary>
    /// Reads an enum value with underlying type <see cref="sbyte"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ReadEnumSByte<TEnum>(this ref DataReader reader)
    where TEnum : unmanaged, Enum
    {
        sbyte value = reader.ReadSByte();
        return Unsafe.As<sbyte, TEnum>(ref value);
    }

    /// <summary>
    /// Reads an enum value with underlying type <see cref="short"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ReadEnumInt16<TEnum>(this ref DataReader reader)
    where TEnum : unmanaged, Enum
    {
        short value = reader.ReadInt16();
        return Unsafe.As<short, TEnum>(ref value);
    }

    /// <summary>
    /// Reads an enum value with underlying type <see cref="ushort"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ReadEnumUInt16<TEnum>(this ref DataReader reader)
    where TEnum : unmanaged, Enum
    {
        ushort value = reader.ReadUInt16();
        return Unsafe.As<ushort, TEnum>(ref value);
    }

    /// <summary>
    /// Reads an enum value with underlying type <see cref="int"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ReadEnumInt32<TEnum>(this ref DataReader reader)
    where TEnum : unmanaged, Enum
    {
        int value = reader.ReadInt32();
        return Unsafe.As<int, TEnum>(ref value);
    }

    /// <summary>
    /// Reads an enum value with underlying type <see cref="uint"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ReadEnumUInt32<TEnum>(this ref DataReader reader)
    where TEnum : unmanaged, Enum
    {
        uint value = reader.ReadUInt32();
        return Unsafe.As<uint, TEnum>(ref value);
    }

    /// <summary>
    /// Reads an enum value with underlying type <see cref="long"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ReadEnumInt64<TEnum>(this ref DataReader reader)
    where TEnum : unmanaged, Enum
    {
        long value = reader.ReadInt64();
        return Unsafe.As<long, TEnum>(ref value);
    }

    /// <summary>
    /// Reads an enum value with underlying type <see cref="ulong"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TEnum ReadEnumUInt64<TEnum>(this ref DataReader reader)
    where TEnum : unmanaged, Enum
    {
        ulong value = reader.ReadUInt64();
        return Unsafe.As<ulong, TEnum>(ref value);
    }

    #endregion Enum Types

    #region Array Types

    /// <summary>
    /// Reads a byte array with specified length.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain <paramref name="count"/> remaining bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] ReadBytes(this ref DataReader reader, int count)
    {
        if (count == 0)
        {
            return [];
        }

        if (count < 0 || count > SerializationStaticOptions.Instance.MaxArrayLength || count > reader.BytesRemaining)
        {
            SerializationDiagnostics.Poison(ref reader, "Out of bounds read");
            return [];
        }

        ref byte ptr = ref reader.GetSpanReference(count);
        byte[] result = new byte[count];

        unsafe
        {
            fixed (byte* pSrc = &ptr)
            {
                fixed (byte* pDst = result)
                {
                    Unsafe.CopyBlockUnaligned(pDst, pSrc, (uint)count);
                }
            }
        }

        reader.Advance(count);
        return result;
    }

    /// <summary>
    /// Reads remaining bytes as byte array.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader state is invalid and remaining bytes cannot be consumed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] ReadRemainingBytes(this ref DataReader reader) => reader.ReadBytes(reader.BytesRemaining);

    #endregion Array Types

    #region Generic Unmanaged

    /// <summary>
    /// Reads any unmanaged type directly from buffer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the reader does not contain enough remaining data.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ReadUnmanaged<T>(this ref DataReader reader) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();

        if (reader.BytesRemaining < size) { SerializationDiagnostics.Poison(ref reader, "Out of bounds read"); return default; }
        ref byte srcPtr = ref reader.GetSpanReference(size);
        T value = Unsafe.ReadUnaligned<T>(ref srcPtr);

        reader.Advance(size);

        return value;
    }

    #endregion Generic Unmanaged

    #region Helper Properties

    /// <summary>
    /// Gets remaining byte count without consuming.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Remaining(this ref DataReader reader) => reader.BytesRemaining;

    #endregion Helper Properties
}

