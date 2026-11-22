// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

#pragma warning disable IDE0079
#pragma warning disable CA1859

using System;
using System.Runtime.CompilerServices;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Internal;
using Nalix.Environment.Extensions;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Transforms;

/// <summary>
/// Shared packet compression helpers for LZ4-framed payloads.
/// </summary>
public static class FrameCompression
{
    /// <summary>
    /// Decompresses a framed packet and clears the compressed flag in the resulting buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IBufferLease DecompressFrame(LeaseRef src)
    {
        if (src.Length <= FrameTransformer.Offset)
        {
            Throw.BufferTooSmallForPacket();
        }

        // [SECURITY] A decompressed frame that arrived encrypted is plaintext: scrub it on release.
        IBufferLease dest = BufferLease.Rent(FrameTransformer
                                       .GetDecompressedLength(src.Span[FrameTransformer.Offset..]) + FrameTransformer.Offset, zeroOnDispose: src.EncryptedOnWire);
        dest.EncryptedOnWire = src.EncryptedOnWire;
        try
        {
            FrameTransformer.Decompress(src, dest);

            ref PacketHeader header = ref dest.Span.AsHeaderRef();
            header.Flags &= ~PacketFlags.COMPRESSED;

            return dest;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            dest.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="DecompressFrame(LeaseRef)"/>
    public static IBufferLease DecompressFrame([Borrowed] IBufferLease src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return DecompressFrame(src.AsRef());
    }

    /// <summary>
    /// Attempts to decompress a framed packet without throwing exceptions on failures.
    /// Returns true and a new leased buffer on success, or false on failure.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryDecompressFrame(LeaseRef src, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IBufferLease? dest)
    {
        dest = null;
        if (src.IsDefault)
        {
            return false;
        }

        if (src.Length <= FrameTransformer.Offset)
        {
            return false;
        }

        if (!FrameTransformer.TryGetDecompressedLength(src.Span[FrameTransformer.Offset..], out int decompressedLength))
        {
            return false;
        }

        // [SECURITY] A decompressed frame that arrived encrypted is plaintext: scrub it on release.
        IBufferLease localDest = BufferLease.Rent(decompressedLength + FrameTransformer.Offset, zeroOnDispose: src.EncryptedOnWire);
        localDest.EncryptedOnWire = src.EncryptedOnWire;

        if (!FrameTransformer.TryDecompress(src, localDest))
        {
            localDest.Dispose();
            return false;
        }

        ref PacketHeader header = ref localDest.Span.AsHeaderRef();
        header.Flags &= ~PacketFlags.COMPRESSED;

        dest = localDest;
        return true;
    }

    /// <inheritdoc cref="TryDecompressFrame(LeaseRef, out IBufferLease?)"/>
    public static bool TryDecompressFrame([Borrowed] IBufferLease src, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IBufferLease? dest)
    {
        if (src is null)
        {
            dest = null;
            return false;
        }

        return TryDecompressFrame(src.AsRef(), out dest);
    }

    /// <summary>
    /// Compresses a framed packet and sets the compressed flag in the resulting buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IBufferLease CompressFrame(LeaseRef src)
    {
        if (src.Length <= FrameTransformer.Offset)
        {
            Throw.BufferTooSmallForPacket();
        }

        IBufferLease dest = BufferLease.Rent(FrameTransformer
                                       .GetMaxCompressedSize(src.Length - FrameTransformer.Offset) + FrameTransformer.Offset, zeroOnDispose: false, BufferLease.TransportHeadroom);
        dest.EncryptedOnWire = src.EncryptedOnWire;

        try
        {
            FrameTransformer.Compress(src, dest);

            ref PacketHeader header = ref dest.Span.AsHeaderRef();
            header.Flags |= PacketFlags.COMPRESSED;

            return dest;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            dest.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="CompressFrame(LeaseRef)"/>
    public static IBufferLease CompressFrame([Borrowed] IBufferLease src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return CompressFrame(src.AsRef());
    }
}
