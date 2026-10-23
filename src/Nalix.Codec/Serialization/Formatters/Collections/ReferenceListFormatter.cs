// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.InteropServices;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.Extensions;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization.Formatters.Collections;

/// <summary>
/// Provides serialization and deserialization for <see cref="System.Collections.Generic.List{T}"/>
/// where T is a reference type.
/// </summary>
/// <typeparam name="T">The reference type of list elements.</typeparam>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class ReferenceListFormatter<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicProperties)] T> : IFillableFormatter<System.Collections.Generic.List<T>>
{
    private static IFormatter<T>? s_elementFormatter;

    /// <summary>
    /// Lazily resolves the element formatter to avoid circular static initialization
    /// when this type is instantiated during <see cref="FormatterProvider"/>'s own
    /// static constructor (e.g. for <c>List&lt;object&gt;</c>).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T> GetElementFormatter()
        => s_elementFormatter ??= FormatterProvider.Get<T>();

    private static string DebuggerDisplay => $"ReferenceListFormatter<{typeof(T).FullName}>";

    /// <summary>
    /// Serializes a list of reference type elements into the provided writer.
    /// </summary>
    /// <param name="writer">The serialization writer used to store the serialized data.</param>
    /// <param name="value">The list of reference type elements to serialize.</param>
    /// <exception cref="SerializationFailureException">
    /// Thrown if the underlying formatter for type <typeparamref name="T"/> encounters an error during serialization.
    /// </exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize(ref DataWriter writer, in System.Collections.Generic.List<T> value)
    {
        if (value is null)
        {
            writer.Write(SerializerBounds.Null);
            return;
        }

        int count = value.Count;
        writer.Write(count);

        ReadOnlySpan<T> span = CollectionsMarshal.AsSpan(value);
        IFormatter<T> elem = GetElementFormatter();
        for (int i = 0; i < span.Length; i++)
        {
            elem.Serialize(ref writer, span[i]);
        }
    }

    /// <summary>
    /// Deserializes a list of reference type elements from the provided reader.
    /// </summary>
    /// <param name="reader">The serialization reader containing the data to deserialize.</param>
    /// <returns>The deserialized list of reference type elements, or null if the serialized data represents a null list.</returns>
    /// <exception cref="SerializationFailureException">
    /// Thrown if the list length is out of range or if the underlying formatter for type <typeparamref name="T"/> encounters an error during deserialization.
    /// </exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public System.Collections.Generic.List<T> Deserialize(ref DataReader reader)
    {
        int count = reader.ReadInt32();

        if (count == 0)
        {
            return [];
        }

        if (count == SerializerBounds.Null)
        {
            return null!;
        }

        if (!CollectionGuard.TryEnsureRead(ref reader, count))
        {
            return default!;
        }

        System.Collections.Generic.List<T> list = new(count);
        CollectionsMarshal.SetCount(list, count);
        Span<T> span = CollectionsMarshal.AsSpan(list);
        IFormatter<T> elem = GetElementFormatter();
        for (int i = 0; i < span.Length; i++)
        {
            if (reader.IsFailed)
            {
                return null!;
            }

            span[i] = elem.Deserialize(ref reader);
            if (reader.IsFailed)
            {
                return null!;
            }
        }

        return list;
    }

    /// <inheritdoc/>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Fill(ref DataReader reader, System.Collections.Generic.List<T> value)
    {
        int length = reader.ReadInt32();

        if (length == SerializerBounds.Null || length == 0)
        {
            value.Clear();
            return;
        }

        if (!CollectionGuard.TryEnsureRead(ref reader, length))
        {
            return;
        }

        value.Clear();
        CollectionsMarshal.SetCount(value, length);

        Span<T> span = CollectionsMarshal.AsSpan(value);
        IFormatter<T> fillElem = GetElementFormatter();
        for (int i = 0; i < length; i++)
        {
            if (reader.IsFailed)
            {
                break;
            }

            span[i] = fillElem.Deserialize(ref reader);
            if (reader.IsFailed)
            {
                break;
            }
        }
    }
}
