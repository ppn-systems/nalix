// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.Extensions;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization.Formatters.Collections;

/// <summary>
/// Provides serialization and deserialization logic for
/// <see cref="System.Collections.Generic.Stack{T}"/>.
/// </summary>
/// <typeparam name="T">The type of elements in the stack.</typeparam>
/// <remarks>
/// <para>
/// Wire format:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>[4 bytes]</c> Count (<see cref="int"/>, little-endian)
/// — <c>-1</c> indicates <c>null</c>, <c>0</c> indicates empty stack.
/// </description>
/// </item>
/// <item>
/// <description>
/// For each element in LIFO order (top -> bottom):
/// <list type="bullet">
/// <item><description>Element serialized using <see cref="IFormatter{T}"/>.</description></item>
/// </list>
/// </description>
/// </item>
/// </list>
/// <para>
/// LIFO order is preserved: after deserialize, <c>Pop()</c> returns the same element
/// that was on top before serialization.
/// </para>
/// </remarks>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class StackFormatter<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicProperties)] T>
    : IFillableFormatter<System.Collections.Generic.Stack<T>?>
{
    private static IFormatter<T>? s_elementFormatter;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T> GetElementFormatter() => s_elementFormatter ??= FormatterProvider.Get<T>();
    private static string DebuggerDisplay => $"StackFormatter<{typeof(T).Name}>";

    /// <summary>
    /// Initializes a new instance of the <see cref="StackFormatter{T}"/> class.
    /// </summary>
    /// <exception cref="SerializationFailureException">
    /// Thrown when <typeparamref name="T"/> is a class other than <see cref="string"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Element type restrictions:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Allowed: primitive types, <see cref="string"/>, enums, unmanaged structs.</description></item>
    /// <item><description>Not allowed: reference types (except <see cref="string"/>).</description></item>
    /// </list>
    /// </remarks>
    public StackFormatter()
    {
        Type elementType = typeof(T);

        if (elementType.IsClass && elementType != typeof(string))
        {
            throw new SerializationFailureException(
                $"StackFormatter: T='{elementType.Name}' is a class — only supports primitive, string, enum, or unmanaged struct as element.");
        }
    }

    // ------------------------------------------------------------------ //
    //  Serialize
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Serializes a stack into the specified <see cref="DataWriter"/>.
    /// </summary>
    /// <param name="writer">The writer to which data will be written.</param>
    /// <param name="value">The stack to serialize. Can be <c>null</c>.</param>
    /// <exception cref="InvalidOperationException">Thrown when the target writer cannot expand or the element formatter cannot be resolved.</exception>
    /// <remarks>
    /// <para>
    /// Serialization behavior:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>null</c> -> writes <c>-1</c> as count.</description></item>
    /// <item><description>Empty stack -> writes <c>0</c>.</description></item>
    /// <item><description>
    /// Otherwise writes count followed by elements in LIFO order (top -> bottom).
    /// </description></item>
    /// </list>
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize(ref DataWriter writer, in System.Collections.Generic.Stack<T>? value)
    {
        if (value is null)
        {
            writer.Write(SerializerBounds.Null);
            return;
        }

        int count = value.Count;
        writer.Write(count);

        if (count is 0)
        {
            return;
        }

        // Stack<T> enumerate theo LIFO (top -> bottom)
        // -> Push() khi deserialize theo thứ tự ngược lại sẽ phục hồi đúng stack
        foreach (T element in value)
        {
            GetElementFormatter().Serialize(ref writer, element);
        }
    }

    // ------------------------------------------------------------------ //
    //  Deserialize
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Deserializes a stack from the specified <see cref="DataReader"/>.
    /// </summary>
    /// <param name="reader">The reader containing serialized data.</param>
    /// <returns>
    /// A reconstructed stack with original LIFO order preserved, or <c>null</c> if the input represents null.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when the element formatter cannot be resolved.</exception>
    /// <exception cref="SerializationFailureException">Thrown when the reader does not contain enough bytes for the declared element count.</exception>
    /// <remarks>
    /// <para>
    /// Deserialization behavior:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>-1</c> -> returns <c>null</c>.</description></item>
    /// <item><description><c>0</c> -> returns an empty stack.</description></item>
    /// <item><description>
    /// Otherwise reads elements into a temporary array then pushes in reverse
    /// to restore original top-of-stack correctly.
    /// </description></item>
    /// </list>
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public System.Collections.Generic.Stack<T>? Deserialize(ref DataReader reader)
    {
        int count = reader.ReadInt32();

        if (count == SerializerBounds.Null)
        {
            return null;
        }

        if (!CollectionGuard.TryEnsureRead(ref reader, count))
        {
            return default;
        }

        System.Collections.Generic.Stack<T> stack = new(count);

        if (count is 0)
        {
            return stack;
        }

        // Serialize ghi top -> bottom
        // Nếu Push() thẳng thì stack bị đảo ngược -> cần đọc vào array rồi Push ngược lại
        T[] buffer = new T[count];
        for (int i = 0; i < count; i++)
        {
            if (reader.IsFailed)
            {
                return null!;
            }

            buffer[i] = GetElementFormatter().Deserialize(ref reader);
            if (reader.IsFailed)
            {
                return null!;
            }
        }

        // Push từ bottom -> top để khôi phục đúng thứ tự LIFO ban đầu
        for (int i = count - 1; i >= 0; i--)
        {
            stack.Push(buffer[i]);
        }

        return stack;
    }

    /// <inheritdoc/>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Fill(ref DataReader reader, System.Collections.Generic.Stack<T>? value)
    {
        if (value is null)
        {
            return;
        }

        int count = reader.ReadInt32();
        value.Clear();

        if (count == SerializerBounds.Null || count == 0)
        {
            return;
        }

        if (!CollectionGuard.TryEnsureRead(ref reader, count))
        {
            return;
        }

        T[] buffer = new T[count];
        for (int i = 0; i < count; i++)
        {
            if (reader.IsFailed)
            {
                return;
            }

            buffer[i] = GetElementFormatter().Deserialize(ref reader);
            if (reader.IsFailed)
            {
                return;
            }
        }

        for (int i = count - 1; i >= 0; i--)
        {
            value.Push(buffer[i]);
        }
    }
}
