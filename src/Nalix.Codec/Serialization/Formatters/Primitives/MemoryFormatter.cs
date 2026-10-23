// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.Extensions;
using Nalix.Codec.Internal;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization.Formatters.Primitives;

/// <summary>
/// Serializes unmanaged memory blocks as a length-prefixed raw byte payload.
/// <see cref="System.Memory{T}"/> and <see cref="System.ReadOnlyMemory{T}"/> use
/// the same wire format so callers can choose mutability without changing bytes on the wire.
/// </summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
/// <remarks>
/// The wire format is:
/// <list type="bullet">
/// <item><description>4-byte little-endian length prefix.</description></item>
/// <item><description>Raw element bytes copied as-is from the underlying span.</description></item>
/// </list>
/// A non-positive length is treated as empty because <see cref="System.Memory{T}"/> itself
/// does not distinguish between default and empty in a way that matters on the wire.
/// </remarks>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class MemoryFormatter<T> : IFormatter<System.Memory<T>>
    where T : unmanaged
{
    private static readonly int s_elementSize = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
    private static string DebuggerDisplay => $"MemoryFormatter<{typeof(T).Name}>";

    // ------------------------------------------------------------------ //
    //  Serialize
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Serializes a <see cref="System.Memory{T}"/> into the specified <see cref="DataWriter"/>.
    /// </summary>
    /// <param name="writer">The writer to which data will be written.</param>
    /// <param name="value">The memory segment to serialize.</param>
    /// <remarks>
    /// The serializer writes the length first, then copies the raw element bytes directly
    /// using <see cref="System.Runtime.InteropServices.MemoryMarshal.AsBytes{T}(System.ReadOnlySpan{T})"/>.
    /// That keeps the path allocation-free and avoids per-element formatting overhead.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize(ref DataWriter writer, in System.Memory<T> value)
    {
        int length = value.Length;
        writer.Write(length);

        // Zero length means there is no payload to copy.
        if (length is 0)
        {
            return;
        }

        // Reinterpret the memory as bytes and copy it in one block.
        System.ReadOnlySpan<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(value.Span);

        writer.Expand(bytes.Length);
        bytes.CopyTo(writer.FreeBuffer);
        writer.Advance(bytes.Length);
    }

    // ------------------------------------------------------------------ //
    //  Deserialize
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Deserializes a <see cref="System.Memory{T}"/> from the specified <see cref="DataReader"/>.
    /// </summary>
    /// <param name="reader">The reader containing serialized data.</param>
    /// <returns>
    /// A <see cref="System.Memory{T}"/> wrapping a newly allocated array,
    /// or <see cref="System.Memory{T}.Empty"/> if the encoded length is not positive.
    /// </returns>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public System.Memory<T> Deserialize(ref DataReader reader)
    {
        int length = reader.ReadInt32();

        // Non-positive lengths map to empty memory on the receiving side.
        if (length <= 0)
        {
            return System.Memory<T>.Empty;
        }

        if (!CollectionGuard.TryEnsureRead(ref reader, length, s_elementSize))
        {
            return default;
        }

        int byteCount = length * s_elementSize;

        // Allocate once and copy the raw payload block directly into the array.
        T[] array = System.GC.AllocateUninitializedArray<T>(length);

        ref byte src = ref reader.GetSpanReference(byteCount);
        ref T first = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array);
        ref byte dst = ref System.Runtime.CompilerServices.Unsafe.As<T, byte>(ref first);

        System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(ref dst, ref src, (uint)byteCount);
        reader.Advance(byteCount);

        return System.MemoryExtensions.AsMemory(array);
    }
}

// --------------------------------------------------------------------------

/// <summary>
/// Serializes <see cref="System.ReadOnlyMemory{T}"/> using the same wire format
/// as <see cref="MemoryFormatter{T}"/>.
/// </summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
/// <remarks>
/// Delegates to <see cref="MemoryFormatter{T}"/> internally —
/// wire format is identical to <see cref="System.Memory{T}"/>.
/// </remarks>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class ReadOnlyMemoryFormatter<T> : IFormatter<System.ReadOnlyMemory<T>>
    where T : unmanaged
{
    private static string DebuggerDisplay => $"ReadOnlyMemoryFormatter<{typeof(T).Name}>";

    private static readonly MemoryFormatter<T> s_inner = new();

    /// <summary>
    /// Serializes a <see cref="System.ReadOnlyMemory{T}"/> into the specified <see cref="DataWriter"/>.
    /// </summary>
    /// <param name="writer">The writer to which data will be written.</param>
    /// <param name="value">The memory value to serialize.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Serialize(ref DataWriter writer, in System.ReadOnlyMemory<T> value) => s_inner.Serialize(ref writer, System.Runtime.InteropServices.MemoryMarshal.AsMemory(value));

    /// <summary>
    /// Deserializes a <see cref="System.ReadOnlyMemory{T}"/> from the specified <see cref="DataReader"/>.
    /// </summary>
    /// <param name="reader">The reader containing serialized data.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public System.ReadOnlyMemory<T> Deserialize(ref DataReader reader) => s_inner.Deserialize(ref reader);
}

