// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nalix.Codec.Internal.Memory;

namespace Nalix.Codec.Internal.LZ4;

/// <summary>
/// Writes literal byte sequences into a destination pointer and advances that pointer
/// as bytes are copied.
/// The helper keeps the hot path small by delegating the actual copy to <see cref="MemOps"/>.
/// </summary>
[SkipLocalsInit]
[EditorBrowsable(EditorBrowsableState.Never)]
internal static unsafe class LiteralWriter
{
    /// <summary>
    /// Writes a sequence of literal bytes from a raw pointer into the destination.
    /// </summary>
    /// <param name="destPtr">The destination pointer, which is advanced by <paramref name="length"/> bytes.</param>
    /// <param name="literalStartPtr">The source pointer containing the literal bytes.</param>
    /// <param name="length">The number of bytes to copy.</param>
    /// <remarks>
    /// The caller owns the destination bounds. This helper does not validate capacity;
    /// it only performs the copy and advances the write cursor.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(
        ref byte* destPtr,
        byte* literalStartPtr,
        int length)
    {
        if ((uint)length == 0u)
        {
            return;
        }

        // Copy the data in one block so callers can treat this as a pointer-based append.
        MemOps.Copy(literalStartPtr, destPtr, length);

        // Advance the cursor past the bytes that were just written.
        destPtr += length;
    }

    /// <summary>
    /// Writes a sequence of literal bytes from a read-only span into the destination.
    /// </summary>
    /// <param name="destPtr">The destination pointer, which is advanced by the copied length.</param>
    /// <param name="literals">The source bytes to append.</param>
    /// <remarks>
    /// This overload is the safer call site for span-based sources but uses the same
    /// pointer append semantics as the raw pointer overload.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(
        ref byte* destPtr,
        ReadOnlySpan<byte> literals)
    {
        if (literals.Length == 0)
        {
            return;
        }

        // Copy span data directly into the pointer destination.
        MemOps.Copy(literals, destPtr);

        // Advance the cursor to the new end of written data.
        destPtr += literals.Length;
    }
}
