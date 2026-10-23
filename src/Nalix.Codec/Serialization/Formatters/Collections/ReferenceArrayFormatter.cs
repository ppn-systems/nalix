// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Codec.Extensions;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization.Formatters.Collections;

/// <summary>
/// Provides serialization and deserialization for arrays of reference types.
/// </summary>
/// <typeparam name="T">The type of the array elements.</typeparam>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class ReferenceArrayFormatter<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicProperties)] T> : IFormatter<T[]>
{
    private static IFormatter<T>? s_elementFormatter;

    /// <summary>
    /// Lazily resolves the element formatter to avoid circular static initialization
    /// when this type is instantiated during <see cref="FormatterProvider"/>'s own
    /// static constructor (e.g. for <c>object[]</c>).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T> GetElementFormatter()
        => s_elementFormatter ??= FormatterProvider.Get<T>();

    private static string DebuggerDisplay => $"ReferenceArrayFormatter<{typeof(T).FullName}>";

    /// <summary>
    /// Serializes an array of reference type objects into the provided writer.
    /// </summary>
    /// <param name="writer">The serialization writer used to store the serialized data.</param>
    /// <param name="value">The array of reference type objects to serialize.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize(ref DataWriter writer, in T[] value)
    {
        if (value == null)
        {
            writer.Write(SerializerBounds.Null);
            return;
        }

        writer.Write(value.Length);

        if (value.Length == 0)
        {
            return;
        }

        System.ReadOnlySpan<T> span = value;
        IFormatter<T> elem = GetElementFormatter();
        for (int i = 0; i < span.Length; i++)
        {
            elem.Serialize(ref writer, span[i]);
        }
    }

    /// <summary>
    /// Deserializes an array of reference type objects from the provided reader.
    /// </summary>
    /// <param name="reader">The serialization reader containing the data to deserialize.</param>
    /// <returns>The deserialized array of reference type objects, or null if the serialized data represents a null array.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public T[] Deserialize(ref DataReader reader)
    {
        int length = reader.ReadInt32();

        if (length == SerializerBounds.Null)
        {
            return null!;
        }

        if (length == 0)
        {
            return [];
        }

        if (!CollectionGuard.TryEnsureRead(ref reader, length))
        {
            return default!;
        }

        T[] array = new T[length];
        IFormatter<T> elem = GetElementFormatter();
        for (int i = 0; i < length; i++)
        {
            if (reader.IsFailed)
            {
                return null!;
            }

            array[i] = elem.Deserialize(ref reader);
            if (reader.IsFailed)
            {
                return null!;
            }
        }

        return array;
    }
}
