// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.InteropServices;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.Extensions;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization.Formatters.Collections;

/// <summary>
/// Serializes/deserializes a List of nullable value-type elements (List&lt;T?&gt; where T : struct).
/// Writes a 1-byte null flag per element: 0 = null, 1 = present.
/// </summary>
/// <typeparam name="T"></typeparam>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class NullableValueListFormatter<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicProperties)] T>
    : IFillableFormatter<System.Collections.Generic.List<T?>> where T : struct
{
    private static IFormatter<T>? s_elementFormatter;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T> GetElementFormatter() => s_elementFormatter ??= FormatterProvider.Get<T>();
    private static string DebuggerDisplay => $"NullableValueListFormatter<{typeof(T).FullName}?>";

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize(ref DataWriter writer, in System.Collections.Generic.List<T?> value)
    {
        if (value == null)
        {
            writer.Write(SerializerBounds.Null);
            return;
        }

        int count = value.Count;
        writer.Write(count);

        if (count == 0)
        {
            return;
        }

        System.ReadOnlySpan<T?> span = CollectionsMarshal.AsSpan(value);

        for (int i = 0; i < span.Length; i++)
        {
            T? item = span[i];
            if (!item.HasValue)
            {
                writer.Write((byte)0);
                continue;
            }

            writer.Write((byte)1);
            GetElementFormatter().Serialize(ref writer, item.Value);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public System.Collections.Generic.List<T?> Deserialize(ref DataReader reader)
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

        System.Collections.Generic.List<T?> list = new(length);
        CollectionsMarshal.SetCount(list, length);
        System.Span<T?> span = CollectionsMarshal.AsSpan(list);

        for (int i = 0; i < span.Length; i++)
        {
            if (reader.IsFailed)
            {
                return null!;
            }

            byte flag = reader.ReadByte();
            if (flag == 0)
            {
                span[i] = null;
            }
            else if (flag == 1)
            {
                span[i] = GetElementFormatter().Deserialize(ref reader);
            }
            else
            {
                SerializationDiagnostics.Poison(ref reader, "Invalid nullable data!");
                return null!;
            }

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
    public void Fill(ref DataReader reader, System.Collections.Generic.List<T?> value)
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

        Span<T?> span = CollectionsMarshal.AsSpan(value);

        for (int i = 0; i < length; i++)
        {
            if (reader.IsFailed)
            {
                break;
            }

            byte flag = reader.ReadByte();
            if (flag == 0)
            {
                span[i] = null;
            }
            else if (flag == 1)
            {
                span[i] = GetElementFormatter().Deserialize(ref reader);
            }
            else
            {
                SerializationDiagnostics.Poison(ref reader, "Invalid nullable data!");
                break;
            }

            if (reader.IsFailed)
            {
                break;
            }
        }
    }
}
