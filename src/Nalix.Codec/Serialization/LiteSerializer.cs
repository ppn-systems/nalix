// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Internal;
using Nalix.Codec.Serialization.Internal.Types;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization;

/// <summary>
/// Provides serialization and deserialization methods for objects.
/// </summary>
[DebuggerStepThrough]
public static class LiteSerializer
{
    #region APIs

    /// <summary>
    /// Registers a formatter for the specified type.
    /// </summary>
    /// <typeparam name="T">The type for which the formatter is being registered.</typeparam>
    /// <param name="formatter">The formatter to register.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if the provided formatter is null.
    /// </exception>
    public static void Register<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>(
        IFormatter<T> formatter) => FormatterProvider.Register(formatter);

    /// <summary>
    /// Serializes an object into a byte array.
    /// </summary>
    /// <typeparam name="T">The type of object to serialize.</typeparam>
    /// <param name="value">The object to serialize.</param>
    /// <returns>A byte array representing the serialized object.</returns>
    /// <exception cref="SerializationFailureException">
    /// Thrown if serialization encounters an error.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when a required formatter dependency is null during registration or serialization dispatch.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no formatter is available for a fixed-size or variable-length serializable type.
    /// </exception>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [SuppressMessage("Style", "IDE0301:Simplify collection initialization", Justification = "<Pending>")]
    public static byte[] Serialize<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(in T value)
    {
        /*
         * [Fast Path: Unmanaged Types]
         * If the type is unmanaged (blittable), we can simply write its bytes 
         * directly into a fresh buffer. No formatter is needed.
         */
        if (TypeMetadata.IsUnmanaged<T>())
        {
            byte[] array = GC.AllocateUninitializedArray<byte>(TypeMetadata.SizeOf<T>());
            Unsafe.WriteUnaligned(
                ref MemoryMarshal.GetArrayDataReference(array), value);

            return array;
        }

        TypeKind kind = TypeMetadata.TryGetFixedSize<T>(out int fixSize);

        if (kind is TypeKind.FixedSizeSerializable or TypeKind.None)
        {
            int capacity = kind is TypeKind.FixedSizeSerializable ? fixSize : 2048;
            IFormatter<T> formatter = ResolveRootFormatter<T>(value);
            DataWriter writer = new(capacity);

            try
            {
                formatter.Serialize(ref writer, value);
                return writer.WrittenCount == 0 ? Array.Empty<byte>() : writer.ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }

        throw new SerializationFailureException($"TYPE {typeof(T).FullName} is not serializable.");
    }

    /// <summary>
    /// Serializes an object into the provided span.
    /// </summary>
    /// <typeparam name="T">The type of object to serialize.</typeparam>
    /// <param name="value">The object to serialize.</param>
    /// <param name="buffer">The target span to write the serialized data into.</param>
    /// <returns>The number of bytes written into the buffer.</returns>
    /// <exception cref="SerializationFailureException">
    /// Thrown if serialization fails or the buffer is too small.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no formatter is available for a fixed-size serializable type.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown if the type is not supported for span-based serialization.
    /// </exception>
    [Pure]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Serialize<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(in T value, byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // Primitive or unmanaged struct
        if (TypeMetadata.IsUnmanaged<T>())
        {
            int size = TypeMetadata.SizeOf<T>();
            if (buffer.Length < size)
            {
                Throw.BufferTooSmall();
            }

            Unsafe.WriteUnaligned(
                ref MemoryMarshal.GetArrayDataReference(buffer), value);

            return size;
        }

        // Reference or Nullable/Complex types
        return Serialize(value, buffer.AsSpan());
    }

    /// <summary>
    /// Serializes an object into the provided span.
    /// </summary>
    /// <typeparam name="T">The type of object to serialize.</typeparam>
    /// <param name="value">The object to serialize.</param>
    /// <param name="buffer">The target span to write the serialized data into.</param>
    /// <returns>The number of bytes written into the buffer.</returns>
    /// <exception cref="SerializationFailureException">
    /// Thrown if serialization fails or the buffer is too small.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no formatter is available for the resolved formatter-based serialization path.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown if the type is not supported for span-based serialization.
    /// </exception>
    [Pure]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Serialize<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(in T value, Span<byte> buffer)
    {
        // ── Case 1: Primitive / unmanaged struct ──────────────────────────────────
        // T is a plain value type with no references (e.g. int, float, custom struct).
        // Write the value directly into the span using unaligned write for performance.
        if (TypeMetadata.IsUnmanaged<T>())
        {
            int size = TypeMetadata.SizeOf<T>();

            if (buffer.Length < size)
            {
                Throw.BufferTooSmall();
            }

            Unsafe.WriteUnaligned(
                ref MemoryMarshal.GetReference(buffer), value);

            return size;
        }

        TypeKind kind = TypeMetadata.TryGetFixedSize<T>(out int fixedSize);

        // ── Case 3: Fixed-size serializable (implements IFixedSizeSerializable) ───
        // T declares a compile-time-known byte size via IFixedSizeSerializable.Size.
        // We can safely wrap the caller's span in a DataWriter without the risk of Expand().
        if (kind is TypeKind.FixedSizeSerializable)
        {
            if (buffer.Length < fixedSize)
            {
                Throw.BufferTooSmall();
            }

            // DataWriter(Span<byte>) wraps the span directly — zero allocation, no pool renting.
            // The formatter must not write more than fixedSize bytes, so Expand() is never called.

            DataWriter writer = new(buffer);
            ResolveRootFormatter<T>(value).Serialize(ref writer, value);
            return writer.WrittenCount;
        }

        // ── Case 4: TypeKind.None — variable-length / composite types ────────────
        // Cannot determine required buffer size at compile time.
        // Wrap the caller's span directly in DataWriter — zero allocation, no intermediate copy.
        // If the formatter writes more than buffer.Length, DataWriter will throw InvalidOperationException
        // (because Span-based DataWriter cannot Expand()).
        if (kind is TypeKind.None)
        {
            IFormatter<T> formatter = ResolveRootFormatter<T>(value);

            // DataWriter(Span<byte>) wraps the span directly — no renting, no pool.
            // Expand() is disabled: if formatter overflows, it throws InvalidOperationException.
            DataWriter writer = new(buffer);

            try
            {
                formatter.Serialize(ref writer, value);
                return writer.WrittenCount;
            }
            finally
            {
                writer.Dispose();
            }
        }

        throw new SerializationFailureException(
            $"Span<byte> serialization is not supported for variable-length type '{typeof(T).FullName}'. Use Serialize<T>(in T value) to obtain a byte[] instead.");
    }

    /// <summary>
    /// Deserializes an object from a byte array.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize.</typeparam>
    /// <param name="buffer">The byte array containing serialized data.</param>
    /// <param name="value">The reference to the object where deserialized data will be stored.</param>
    /// <returns>The number of bytes read during deserialization.</returns>
    /// <exception cref="SerializationFailureException">
    /// Thrown if deserialization encounters an error or if there is insufficient data in the buffer.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown if the buffer is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no formatter is available for formatter-based deserialization.
    /// </exception>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Deserialize<[
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(byte[] buffer, ref T value)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!UsesFormatterReader<T>())
        {
            return Deserialize<T>((ReadOnlySpan<byte>)buffer, ref value);
        }

        if (buffer.Length == 0)
        {
            Throw.EmptyBuffer();
        }

        DataReader reader = new(buffer);
        DeserializeInternal(ref reader, ref value);

        return reader.BytesRead;
    }

    /// <summary>
    /// Deserializes an object from a byte array.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize.</typeparam>
    /// <param name="buffer">The byte array containing serialized data.</param>
    /// <param name="value">The number of bytes read during deserialization.</param>
    /// <returns>The deserialized object.</returns>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public static T Deserialize<[
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(byte[] buffer, out int value)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!UsesFormatterReader<T>())
        {
            return Deserialize<T>((ReadOnlySpan<byte>)buffer, out value);
        }

        if (buffer.Length == 0)
        {
            Throw.EmptyBuffer();
        }

        IFormatter<T> formatter = ResolveRootFormatterForRead<T>();
        DataReader reader = new(buffer);
        T result = formatter.Deserialize(ref reader);
        if (reader.IsFailed)
        {
            Throw.SerializationFailed(typeof(T), "Deserialization failed due to truncated or malformed payload.");
        }

        value = reader.BytesRead;
        return result;
    }

    /// <summary>
    /// Deserializes an object from a read-only memory buffer.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize.</typeparam>
    /// <param name="buffer">The memory buffer containing serialized data.</param>
    /// <param name="value">The reference to the object where deserialized data will be stored.</param>
    /// <returns>The number of bytes read during deserialization.</returns>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Deserialize<[
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlyMemory<byte> buffer, ref T value)
    {
        if (!UsesFormatterReader<T>())
        {
            return Deserialize<T>(buffer.Span, ref value);
        }

        if (buffer.IsEmpty)
        {
            Throw.EmptyBuffer();
        }

        IFormatter<T> formatter = RootFormatterCache<T>.Formatter;
        IFillableFormatter<T>? fillable = RootFormatterCache<T>.Fillable;

        DataReader reader = new(buffer);
        if (value is not null && fillable is not null)
        {
            fillable.Fill(ref reader, value);
        }
        else
        {
            value = formatter.Deserialize(ref reader);
        }

        if (reader.IsFailed)
        {
            Throw.SerializationFailed(typeof(T), "Deserialization failed due to truncated or malformed payload.");
        }

        return reader.BytesRead;
    }

    /// <summary>
    /// Deserializes an object from a read-only memory buffer.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize.</typeparam>
    /// <param name="buffer">The memory buffer containing serialized data.</param>
    /// <param name="value">The number of bytes read during deserialization.</param>
    /// <returns>The deserialized object.</returns>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public static T Deserialize<[
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlyMemory<byte> buffer, out int value)
    {
        if (!UsesFormatterReader<T>())
        {
            return Deserialize<T>(buffer.Span, out value);
        }

        if (buffer.IsEmpty)
        {
            Throw.EmptyBuffer();
        }

        IFormatter<T> formatter = ResolveRootFormatterForRead<T>();
        DataReader reader = new(buffer);
        T result = formatter.Deserialize(ref reader);
        if (reader.IsFailed)
        {
            Throw.SerializationFailed(typeof(T), "Deserialization failed due to truncated or malformed payload.");
        }

        value = reader.BytesRead;
        return result;
    }

    /// <summary>
    /// Deserializes an object from a read-only span of bytes.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize.</typeparam>
    /// <param name="buffer">The span containing serialized data.</param>
    /// <param name="value">The reference to the object where deserialized data will be stored.</param>
    /// <returns>The number of bytes read during deserialization.</returns>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Deserialize<[
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlySpan<byte> buffer, ref T value)
    {
        if (buffer.IsEmpty)
        {
            Throw.EmptyBuffer();
        }

        if (TypeMetadata.IsUnmanaged<T>())
        {
            if (buffer.Length < TypeMetadata.SizeOf<T>())
            {
                Throw.EndOfStream();
            }
            value = Unsafe.ReadUnaligned<T>(
                ref MemoryMarshal.GetReference(buffer));

            return TypeMetadata.SizeOf<T>();
        }

        IFormatter<T> formatter = RootFormatterCache<T>.Formatter;
        IFillableFormatter<T>? fillable = RootFormatterCache<T>.Fillable;

        DataReader reader = new(buffer);
        if (value is not null && fillable is not null)
        {
            fillable.Fill(ref reader, value);
        }
        else
        {
            value = formatter.Deserialize(ref reader);
        }

        if (reader.IsFailed)
        {
            Throw.SerializationFailed(typeof(T), "Deserialization failed due to truncated or malformed payload.");
        }

        return reader.BytesRead;
    }

    /// <summary>
    /// Deserializes an object from a byte array.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize.</typeparam>
    /// <param name="buffer">The byte array containing serialized data.</param>
    /// <param name="value">The reference to the object where deserialized data will be stored.</param>
    /// <returns>The number of bytes read during deserialization.</returns>
    /// <exception cref="SerializationFailureException">
    /// Thrown if deserialization encounters an error or if there is insufficient data in the buffer.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown if the buffer is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no formatter is available for formatter-based deserialization.
    /// </exception>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public static T Deserialize<[
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlySpan<byte> buffer, out int value)
    {
        if (buffer.IsEmpty)
        {
            Throw.EmptyBuffer();
        }

        if (TypeMetadata.IsUnmanaged<T>())
        {
            if (buffer.Length < TypeMetadata.SizeOf<T>())
            {
                Throw.EndOfStream();
            }
            value = TypeMetadata.SizeOf<T>();

            return Unsafe.ReadUnaligned<T>(
                ref MemoryMarshal.GetReference(buffer));
        }

        IFormatter<T> formatter = ResolveRootFormatterForRead<T>();
        DataReader reader = new(buffer);
        T result = formatter.Deserialize(ref reader);
        if (reader.IsFailed)
        {
            Throw.SerializationFailed(typeof(T), "Deserialization failed due to truncated or malformed payload.");
        }

        value = reader.BytesRead;
        return result;
    }

    #endregion APIs

    #region Fill API

    /// <summary>
    /// Fills an existing instance from a byte array using in-place deserialization (object reuse path).
    /// Optimized for high-performance object pooling in low-latency TCP messaging receive loops.
    /// </summary>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Fill<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(byte[] buffer, ref T value)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Fill((ReadOnlySpan<byte>)buffer, ref value);
    }

    /// <summary>
    /// Fills an existing instance from a read-only span.
    /// Zero-allocation hot path designed for direct use with socket receive buffers
    /// (e.g. <c>SocketAsyncEventArgs</c>, <c>NetworkStream</c>, memory-mapped recv).
    /// </summary>
    [Pure]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Fill<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlySpan<byte> buffer, ref T value)
    {
        if (buffer.IsEmpty)
        {
            Throw.EmptyBuffer();
        }

        IFillableFormatter<T>? fillable = RootFormatterCache<T>.Fillable ?? throw new SerializationFailureException(
                $"Fill API is not supported for type '{typeof(T).FullName}'. " +
                "Only registered IFillableFormatter implementations, source-generated class formatters, and supported collections are allowed. " +
                "Unmanaged structs and non-fillable value types must use Deserialize<T>(buffer, ref value) instead.");
        if (value is null && typeof(T).IsClass)
        {
            throw new SerializationFailureException(
                $"Cannot Fill a null instance of reference type '{typeof(T).FullName}'. " +
                "Pre-create the target instance from an object pool before calling Fill.");
        }

        DataReader reader = new(buffer);
        fillable.Fill(ref reader, value);
        if (reader.IsFailed)
        {
            Throw.SerializationFailed(typeof(T), "Deserialization failed due to truncated or malformed payload.");
        }

        return reader.BytesRead;
    }

    /// <summary>
    /// Fills an existing instance from a read-only memory buffer.
    /// </summary>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Fill<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlyMemory<byte> buffer, ref T value)
    {
        if (buffer.IsEmpty)
        {
            Throw.EmptyBuffer();
        }

        IFillableFormatter<T>? fillable = RootFormatterCache<T>.Fillable ?? throw new SerializationFailureException(
                $"Fill API is not supported for type '{typeof(T).FullName}'.");
        if (value is null && typeof(T).IsClass)
        {
            throw new SerializationFailureException(
                $"Cannot Fill null reference type '{typeof(T).FullName}'. Pre-create pooled instance first.");
        }

        DataReader reader = new(buffer);
        fillable.Fill(ref reader, value);
        if (reader.IsFailed)
        {
            Throw.SerializationFailed(typeof(T), "Deserialization failed due to truncated or malformed payload.");
        }

        return reader.BytesRead;
    }

    #endregion Fill API

    #region Internal APIs

    /// <summary>
    /// Attempts to deserialize an object from a read-only span of bytes without throwing exceptions.
    /// </summary>
    /// <typeparam name="T">The type of object to deserialize.</typeparam>
    /// <param name="buffer">The span containing serialized data.</param>
    /// <param name="value">The reference to the object where deserialized data will be stored.</param>
    /// <param name="bytesRead">The number of bytes read during deserialization.</param>
    /// <returns>True if deserialization succeeded, otherwise false.</returns>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static bool TryDeserialize<[
        DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlySpan<byte> buffer, ref T value, out int bytesRead)
    {
        bytesRead = 0;

        if (buffer.IsEmpty)
        {
            return false;
        }

        if (TypeMetadata.IsUnmanaged<T>())
        {
            if (buffer.Length < TypeMetadata.SizeOf<T>())
            {
                return false;
            }
            value = Unsafe.ReadUnaligned<T>(
                ref MemoryMarshal.GetReference(buffer));

            bytesRead = TypeMetadata.SizeOf<T>();
            return true;
        }

        IFormatter<T> formatter = RootFormatterCache<T>.Formatter;
        IFillableFormatter<T>? fillable = RootFormatterCache<T>.Fillable;

        DataReader reader = new(buffer);
        if (value is not null && fillable is not null)
        {
            fillable.Fill(ref reader, value);
        }
        else
        {
            value = formatter.Deserialize(ref reader);
        }

        if (reader.IsFailed)
        {
            return false;
        }

        bytesRead = reader.BytesRead;
        return true;
    }

    #endregion Internal APIs

    #region Private Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DeserializeInternal<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ref DataReader reader, ref T value)
    {
        IFormatter<T> formatter = RootFormatterCache<T>.Formatter;
        IFillableFormatter<T>? fillable = RootFormatterCache<T>.Fillable;

        if (value is not null && fillable is not null)
        {
            fillable.Fill(ref reader, value);
        }
        else
        {
            value = formatter.Deserialize(ref reader);
        }

        if (reader.IsFailed)
        {
            Throw.SerializationFailed(typeof(T), "Deserialization failed due to truncated or malformed payload.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T> ResolveRootFormatter<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(in T value)
    {
        /*
         * [Formatter Resolution]
         * We use a static generic cache (RootFormatterCache<T>) to resolve 
         * formatters. This eliminates dictionary lookups on every 
         * serialization call.
         */
        if (RootFormatterCache<T>.ThrowsOnNull && value is null)
        {
            throw new SerializationFailureException(
                $"Cannot serialize null reference type '{typeof(T).FullName}' without an explicit nullable wrapper.");
        }

        return RootFormatterCache<T>.Formatter;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool UsesFormatterReader<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => !TypeMetadata.IsUnmanaged<T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T> ResolveRootFormatterForRead<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => RootFormatterCache<T>.Formatter;

    private static class RootFormatterCache<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>
    {
        public static readonly bool ThrowsOnNull;
        public static readonly IFormatter<T> Formatter;
        public static readonly IFillableFormatter<T>? Fillable;

        static RootFormatterCache()
        {
            Formatter = FormatterProvider.Get<T>();
            ThrowsOnNull = false;

            Fillable = Formatter as IFillableFormatter<T>;
        }
    }

    #endregion Private Methods
}
