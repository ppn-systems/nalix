// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.Extensions;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization.Formatters.Primitives;

/// <summary>
/// Serializes <see cref="string"/> arrays as a length-prefixed sequence of nested
/// string payloads.
/// <para>
/// The first <see cref="ushort"/> stores the array state:
/// 0 means empty, <see cref="SerializerBounds.Null"/> means <see langword="null"/>,
/// and any other value is the element count.
/// </para>
/// </summary>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Diagnostics.DebuggerNonUserCode]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class StringArrayFormatter : IFormatter<string[]>
{
    private static string DebuggerDisplay => "StringFormatter<SYSTEM.String[]>";

    private static IFormatter<string>? s_stringFormatterInstance;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static IFormatter<string> GetStringFormatter() => s_stringFormatterInstance ??= FormatterProvider.Get<string>();

    /// <summary>
    /// Serializes a string array into the provided writer.
    /// </summary>
    /// <remarks>
    /// Each element is written using <see cref="StringFormatter"/> so array
    /// elements follow the same null/empty/non-empty rules as standalone strings.
    /// </remarks>
    /// <param name="writer">The serialization writer used to store the serialized data.</param>
    /// <param name="value">The string array to serialize.</param>
    /// <exception cref="SerializationFailureException">Thrown when <paramref name="value"/> exceeds the maximum encodable element count.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the writer cannot expand or a required nested formatter is unavailable.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize(ref DataWriter writer, in string[] value)
    {
        // Null array is represented by the reserved sentinel value.
        if (value is null)
        {
            writer.Write(SerializerBounds.Null);
            return;
        }

        // Empty arrays are encoded with a zero count and no element payloads.
        if (value.Length == 0)
        {
            writer.Write(0);
            return;
        }

        // Reserve the sentinel value for null, so valid arrays must stay below it.
        if (value.Length > SerializationStaticOptions.Instance.MaxStringLength)
        {
            throw new SerializationFailureException("The string array exceeds the maximum encodable length.");
        }

        writer.Write(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            // Delegate element encoding to the string formatter so one place owns the
            // null/empty/UTF-8 details.
            GetStringFormatter().Serialize(ref writer, value[i]);
        }
    }

    /// <summary>
    /// Deserializes a string array from the provided reader.
    /// </summary>
    /// <remarks>
    /// The array header is decoded first, then each element is delegated back to
    /// <see cref="StringFormatter"/> so the per-string wire format stays consistent.
    /// </remarks>
    /// <param name="reader">The serialization reader containing the data to deserialize.</param>
    /// <returns>The deserialized string array.</returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0301:Simplify collection initialization", Justification = "<Pending>")]
    public string[] Deserialize(ref DataReader reader)
    {
        int length = reader.ReadInt32();

        // Zero means an empty array, not a null array.
        if (length == 0)
        {
            return Array.Empty<string>();
        }

        // The sentinel is reserved for null arrays.
        if (length == SerializerBounds.Null)
        {
            return null!;
        }

        if (length < 0 || length > SerializationStaticOptions.Instance.MaxStringLength || !CollectionGuard.TryEnsureRead(ref reader, length, sizeof(int)))
        {
            SerializationDiagnostics.Poison(ref reader, "Out of bounds read");
            return null!;
        }

        string[] result = new string[length];

        for (int i = 0; i < length; i++)
        {
            if (reader.IsFailed)
            {
                return null!;
            }

            // Each element is decoded using the same rules as standalone strings.
            result[i] = GetStringFormatter().Deserialize(ref reader);
            if (reader.IsFailed)
            {
                return null!;
            }
        }

        return result;
    }
}

