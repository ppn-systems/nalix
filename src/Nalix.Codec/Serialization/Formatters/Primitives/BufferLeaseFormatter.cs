// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Codec.Extensions;
using Nalix.Codec.Serialization.Internal;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Serialization.Formatters.Primitives;

/// <summary>
/// Serializes a <see cref="BufferLease"/> as a length-prefixed raw byte payload, without allocating a
/// <c>byte[]</c> on either side: the writer copies from the lease, and the reader copies into a buffer
/// rented from the buffer pool.
/// </summary>
/// <remarks>
/// The wire format is a 4-byte little-endian length followed by the raw bytes; a null lease is written as
/// <see cref="SerializerBounds.Null"/>. The caller that receives a deserialized lease owns it and must
/// dispose it (a pooled packet does so in <c>ResetForPool</c>).
/// </remarks>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerStepThrough]
[System.Runtime.CompilerServices.SkipLocalsInit]
internal sealed class BufferLeaseFormatter : IFormatter<BufferLease?>
{
    /// <inheritdoc/>
    public void Serialize(ref DataWriter writer, in BufferLease? value)
    {
        if (value is null)
        {
            writer.Write(SerializerBounds.Null);
            return;
        }

        System.ReadOnlySpan<byte> bytes = value.Span;
        writer.Write(bytes.Length);
        if (bytes.Length == 0)
        {
            return;
        }

        writer.Expand(bytes.Length);
        bytes.CopyTo(writer.FreeBuffer);
        writer.Advance(bytes.Length);
    }

    /// <inheritdoc/>
    public BufferLease? Deserialize(ref DataReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0)
        {
            return null;
        }

        if (length == 0)
        {
            return BufferLease.Rent(0);
        }

        if (!CollectionGuard.TryEnsureRead(ref reader, length, sizeof(byte)))
        {
            return null;
        }

        ref byte first = ref reader.GetSpanReference(length);
        BufferLease lease = BufferLease.CopyFrom(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref first, length));
        reader.Advance(length);
        return lease;
    }
}
