// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Extensions;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization.Formatters.Primitives;

/// <summary>
/// Serializes nullable value types as a one-byte presence flag followed by the
/// underlying value when present.
/// </summary>
/// <typeparam name="T">
/// The underlying value type, which must have a registered formatter.
/// </typeparam>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class NullableFormatter<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicProperties)] T> : IFormatter<T?> where T : struct
{
    private static IFormatter<T>? s_valueFormatter;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T> GetValueFormatter() => s_valueFormatter ??= FormatterProvider.Get<T>();

    #region Constants

    /// <summary>
    /// Presence flag used when the nullable value has no payload.
    /// </summary>
    private const byte NoValueFlag = 0;

    /// <summary>
    /// Presence flag used when the nullable value does have a payload.
    /// </summary>
    private const byte HasValueFlag = 1;

    #endregion Constants

    #region Fields

    private static string DebuggerDisplay => $"NullableFormatter<{typeof(T).FullName}>";

    #endregion Fields

    /// <summary>
    /// Serializes a nullable value into the provided writer.
    /// </summary>
    /// <remarks>
    /// The flag is written first so the reader can decide whether to stop at the
    /// marker or continue into the underlying formatter.
    /// </remarks>
    /// <param name="writer">The serialization writer used to store the serialized data.</param>
    /// <param name="value">The nullable value to serialize.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize(ref DataWriter writer, in T? value)
    {
        // Emit a compact presence marker before any payload bytes.
        writer.Write(value.HasValue ? HasValueFlag : NoValueFlag);

        if (value.HasValue)
        {
            GetValueFormatter().Serialize(ref writer, value.Value);
        }
    }

    /// <summary>
    /// Deserializes a nullable value from the provided reader.
    /// </summary>
    /// <remarks>
    /// The one-byte marker is validated before the underlying formatter is called,
    /// which lets corrupt nullable payloads fail fast with a clear exception.
    /// </remarks>
    /// <param name="reader">The serialization reader containing the data to deserialize.</param>
    /// <returns>The deserialized nullable value.</returns>
    /// <exception cref="SerializationFailureException">
    /// Thrown if the nullable data is invalid or has an unexpected format.
    /// </exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public T? Deserialize(ref DataReader reader)
    {
        byte hasValue = reader.ReadByte();

        if (hasValue == NoValueFlag)
        {
            return null;
        }
        else if (hasValue != HasValueFlag)
        {
            SerializationDiagnostics.Poison(ref reader, "Invalid nullable data!");
            return null;
        }
        else
        {
            // Delegate the actual value decoding to the registered formatter for T.
            return GetValueFormatter().Deserialize(ref reader);
        }
    }
}
