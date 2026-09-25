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
using Nalix.Abstractions.Security;
using Nalix.Codec.Internal;
using Nalix.Codec.Security;
using Nalix.Environment.Extensions;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Transforms;

/// <summary>
/// Shared packet cipher helpers for encrypting and decrypting framed payloads.
/// </summary>
public static class FrameCipher
{
    /// <summary>
    /// Decrypts a framed packet. The ENCRYPTED flag is intentionally left set on the
    /// resulting buffer's header so downstream dispatch can still tell the frame arrived
    /// encrypted on the wire (see <see cref="PacketFlags.ENCRYPTED"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IBufferLease DecryptFrame(
        [Borrowed] IBufferLease src,
        ReadOnlySpan<byte> key, CipherSuiteType expectedAlgorithm, out uint seq)
    {
        ArgumentNullException.ThrowIfNull(src);

        if (src.Length < FrameTransformer.Offset + EnvelopeCipher.HeaderSize)
        {
            Throw.CiphertextFrameTooShort();
        }

        // [SECURITY] Holds decrypted plaintext: scrub the used range when the lease is released.
        IBufferLease dest = BufferLease.Rent(FrameTransformer.Offset + FrameTransformer
                                       .GetPlaintextLength(src.Span), zeroOnDispose: true);
        dest.IsReliable = src.IsReliable;
        try
        {
            FrameTransformer.Decrypt(src, dest, key, expectedAlgorithm, out seq);

            // The frame authenticated successfully, so it did arrive encrypted on the wire.
            // Record that on the lease (survives further transforms like decompression)
            // separately from the header's ENCRYPTED flag, which is transform-internal
            // bookkeeping and is cleared below.
            dest.EncryptedOnWire = true;

            ref PacketHeader header = ref dest.Span.AsHeaderRef();
            header.Flags &= ~PacketFlags.ENCRYPTED;

            return dest;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            dest.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attempts to decrypt a framed packet without throwing exceptions on authentication failures.
    /// Returns true and a new leased buffer on success, or false on failure.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryDecryptFrame(
        [Borrowed] IBufferLease src,
        ReadOnlySpan<byte> key, CipherSuiteType expectedAlgorithm,
        out uint seq,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IBufferLease? dest)
    {
        dest = null;
        seq = 0;

        if (src is null)
        {
            return false;
        }

        if (src.Length < FrameTransformer.Offset + EnvelopeCipher.HeaderSize)
        {
            return false;
        }

        if (!FrameTransformer.TryGetPlaintextLength(src.Span, out int plaintextLength))
        {
            return false;
        }

        // [SECURITY] Holds decrypted plaintext: scrub the used range when the lease is released.
        IBufferLease localDest = BufferLease.Rent(FrameTransformer.Offset + plaintextLength, zeroOnDispose: true);
        localDest.IsReliable = src.IsReliable;

        if (!FrameTransformer.TryDecrypt(src, localDest, key, expectedAlgorithm, out seq))
        {
            localDest.Dispose();
            return false;
        }

        // Record the on-wire encrypted state on the lease (see DecryptFrame remarks),
        // then clear the header's transient ENCRYPTED flag.
        localDest.EncryptedOnWire = true;

        ref PacketHeader header = ref localDest.Span.AsHeaderRef();
        header.Flags &= ~PacketFlags.ENCRYPTED;

        dest = localDest;
        return true;
    }

    /// <summary>
    /// Encrypts a framed packet and sets the encrypted flag in the resulting buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IBufferLease EncryptFrame([Borrowed] IBufferLease src, ReadOnlySpan<byte> key, uint? seq, CipherSuiteType suite)
    {
        ArgumentNullException.ThrowIfNull(src);

        // Ciphertext only; reserve transport headroom so the wire header is written in place.
        IBufferLease dest = BufferLease.Rent(FrameTransformer.Offset + FrameTransformer
                                       .GetMaxCiphertextSize(suite, src.Length - FrameTransformer.Offset), zeroOnDispose: false, BufferLease.TransportHeadroom);
        dest.IsReliable = src.IsReliable;
        try
        {
            FrameTransformer.Encrypt(src, dest, key, seq, suite);

            ref PacketHeader header = ref dest.Span.AsHeaderRef();
            header.Flags |= PacketFlags.ENCRYPTED;

            return dest;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            dest.Dispose();
            throw;
        }
    }
}
