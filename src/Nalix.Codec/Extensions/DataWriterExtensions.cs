// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Extensions;

/// <summary>
/// Extension methods for writing primitive and Abstractions types to <see cref="DataWriter"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DataWriterExtensions
{
    #region Cache

    private static class EnumCache<TEnum> where TEnum : Enum
    {
        public static readonly int Size = Unsafe.SizeOf<TEnum>();
    }

    #endregion Cache

    #region Primitive Types

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, sbyte value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, byte value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, short value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, ushort value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, uint value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, int value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, long value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, ulong value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, bool value) => writer.Write((byte)(value ? 1 : 0));

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, char value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, float value) => writer.Write(value);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, double value) => writer.Write(value);

    #endregion Primitive Types

    #region Enum Types

    /// <summary>
    /// Writes an enum using the width of its underlying type (1, 2, 4 or 8 bytes).
    /// </summary>
    /// <typeparam name="TEnum"></typeparam>
    /// <param name="writer"></param>
    /// <param name="value"></param>
    /// <exception cref="NotSupportedException">Thrown when <typeparamref name="TEnum"/> has an underlying size other than 1, 2, 4 or 8 bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteEnum<TEnum>(this ref DataWriter writer, TEnum value) where TEnum : Enum
    {
        int size = EnumCache<TEnum>.Size;

        ref TEnum valRef = ref value;

        if (size == sizeof(byte))
        {
            writer.Write(Unsafe.As<TEnum, byte>(ref valRef));
            return;
        }

        if (size == sizeof(ushort))
        {
            writer.Write(Unsafe.As<TEnum, ushort>(ref valRef));
            return;
        }

        if (size == sizeof(uint))
        {
            writer.Write(Unsafe.As<TEnum, uint>(ref valRef));
            return;
        }

        if (size == sizeof(int))
        {
            writer.Write(Unsafe.As<TEnum, int>(ref valRef));
            return;
        }

        if (size == sizeof(ulong))
        {
            writer.Write(Unsafe.As<TEnum, ulong>(ref valRef));
            return;
        }

        throw new NotSupportedException($"Enum size {size} is not supported.");
    }

    #endregion Enum Types

    #region Array Types

    /// <summary>
    /// Writes a byte array to the buffer.
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="value"></param>
    /// <exception cref="InvalidOperationException">Thrown when the writer wraps a fixed buffer that cannot expand to fit <paramref name="value"/>.</exception>
    /// <exception cref="OutOfMemoryException">Thrown when the writer cannot rent a larger backing buffer.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, byte[] value) => writer.Write(value);

    /// <summary>
    /// Writes a span of bytes to the buffer.
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="value"></param>
    /// <exception cref="InvalidOperationException">Thrown when the writer wraps a fixed buffer that cannot expand to fit <paramref name="value"/>.</exception>
    /// <exception cref="OutOfMemoryException">Thrown when the writer cannot rent a larger backing buffer.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(this ref DataWriter writer, ReadOnlySpan<byte> value) => writer.Write(value);

    #endregion Array Types

    #region Generic Unmanaged

    /// <summary>
    /// Writes any unmanaged type directly to buffer.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="writer"></param>
    /// <param name="value"></param>
    /// <exception cref="InvalidOperationException">Thrown when the writer wraps a fixed buffer that cannot expand to fit the unmanaged value.</exception>
    /// <exception cref="OutOfMemoryException">Thrown when the writer cannot rent a larger backing buffer.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUnmanaged<T>(this ref DataWriter writer, T value) where T : unmanaged => writer.Write(value);

    #endregion Generic Unmanaged
}
