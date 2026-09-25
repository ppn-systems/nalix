// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

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
/// Unifies the execution of cryptographic and compression transforms for inbound and outbound frames.
/// </summary>
public static class FramePipeline
{
    /// <summary>
    /// A compressed payload is only sent when it is at least 1/<c>2^MinGainShift</c> (12.5 %) smaller
    /// than the original. Otherwise the frame goes out uncompressed (no <c>COMPRESSED</c> flag), which
    /// is wire-compatible: receivers only decompress frames that carry the flag.
    /// </summary>
    private const int MinGainShift = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCompressionWorthwhile(int compressedLength, int originalLength)
        => compressedLength <= originalLength - (originalLength >> MinGainShift);

    /// <summary>
    /// Applies inbound transforms in transport order: decrypt first, then decompress.
    /// Mutates the <paramref name="current"/> lease directly via <see langword="ref"/> to optimize performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ProcessInbound(
        [Borrowed] ref IBufferLease current,
        ReadOnlySpan<byte> secret, CipherSuiteType algorithm, out uint? seq)
    {
        if (!TryProcessInbound(ref current, secret, algorithm, out seq))
        {
            Throw.InboundPipelineFailed();
        }
    }

    /// <summary>
    /// Attempts to apply inbound transforms in transport order without throwing exceptions on failures.
    /// Returns true on success, or false if decryption, decompression, or validation fails.
    /// Mutates the <paramref name="current"/> lease directly via <see langword="ref"/> to optimize performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryProcessInbound(
        [Borrowed] ref IBufferLease current,
        ReadOnlySpan<byte> secret, CipherSuiteType algorithm, out uint? seq)
    {
        if (current is null)
        {
            seq = null;
            return false;
        }

        seq = null;
        IBufferLease original = current;
        PacketFlags flags = current.Span.AsHeaderRef().Flags;

        bool isEncrypted = (flags & PacketFlags.ENCRYPTED) != 0;
        bool isCompressed = (flags & PacketFlags.COMPRESSED) != 0;

        if (isEncrypted && isCompressed)
        {
            if (algorithm == CipherSuiteType.None)
            {
                return false;
            }

            if (secret.IsEmpty)
            {
                return false;
            }

            if (!TryProcessInboundFused(ref current, secret, algorithm, out uint _seq))
            {
                return false;
            }
            seq = _seq;
            return true;
        }

        if (isEncrypted)
        {
            if (algorithm == CipherSuiteType.None)
            {
                return false;
            }

            if (secret.IsEmpty)
            {
                return false;
            }

            if (!FrameCipher.TryDecryptFrame(current, secret, algorithm, out uint _seq, out IBufferLease? decrypted))
            {
                return false;
            }

            current = decrypted;
            seq = _seq;

            // Re-read flags after decryption since the inner payload might have other flags (e.g., COMPRESSED).
            flags = current.Span.AsHeaderRef().Flags;
        }

        if ((flags & PacketFlags.COMPRESSED) != 0)
        {
            IBufferLease prev = current;
            if (!FrameCompression.TryDecompressFrame(current, out IBufferLease? decompressed))
            {
                // If we replaced a buffer that was ALREADY a replacement (intermediate),
                // we must dispose it to avoid a leak. We do NOT dispose the 'original' one.
                if (!ReferenceEquals(prev, original))
                {
                    prev.Dispose();
                }
                return false;
            }

            current = decompressed;

            // If we replaced a buffer that was ALREADY a replacement (intermediate),
            // we must dispose it to avoid a leak. We do NOT dispose the 'original' one.
            if (!ReferenceEquals(prev, original))
            {
                prev.Dispose();
            }
        }

        return true;
    }

    private static bool TryProcessInboundFused(
        [Borrowed] ref IBufferLease current,
        ReadOnlySpan<byte> secret, CipherSuiteType algorithm, out uint seq)
    {
        seq = 0;
        ReadOnlySpan<byte> srcSpan = current.Span;

        if (!FrameTransformer.TryGetPlaintextLength(srcSpan, out int decryptedSize))
        {
            return false;
        }

        byte[] tempArr = BufferLease.ByteArrayPool.Rent(decryptedSize);
        try
        {
            Span<byte> tempRegion = tempArr.AsSpan(0, decryptedSize);

            // 1. Decrypt directly into tempRegion
            if (!EnvelopeCipher.TryDecrypt(secret, srcSpan[FrameTransformer.Offset..], tempRegion, null, algorithm, out int decLen, out seq, out _))
            {
                return false;
            }

            // 2. Read the uncompressed length from the LZ4 block header
            if (!FrameTransformer.TryGetDecompressedLength(tempRegion[..decLen], out int decompressedSize))
            {
                return false;
            }

            // 3. Rent final lease for the fully decompressed packet
            // [SECURITY] Holds decrypted plaintext: scrub the used range when the lease is released.
            BufferLease finalLease = BufferLease.Rent(FrameTransformer.Offset + decompressedSize, zeroOnDispose: true);
            finalLease.IsReliable = current.IsReliable;
            finalLease.EncryptedOnWire = true;

            Span<byte> destFull = finalLease.SpanFull;

            // 4. Decompress from tempRegion into the final lease's payload section
            if (!LZ4.LZ4Codec.TryDecode(tempRegion[..decLen], destFull[FrameTransformer.Offset..], out int finalLen))
            {
                finalLease.Dispose();
                return false;
            }

            // 5. Copy the header once
            srcSpan[..FrameTransformer.Offset].CopyTo(destFull[..FrameTransformer.Offset]);

            // 6. Clear both transient flags from the final header. The on-wire encrypted
            // state was already recorded on finalLease.EncryptedOnWire above, so dispatch
            // does not depend on this flag surviving decryption.
            ref PacketHeader header = ref destFull.AsHeaderRef();
            header.Flags &= ~(PacketFlags.COMPRESSED | PacketFlags.ENCRYPTED);

            // 7. Finalize the lease length
            finalLease.CommitLength(FrameTransformer.Offset + finalLen);

            // IMPORTANT: Only swap the reference.
            // DO NOT call current.Dispose() here to preserve the original lease ownership rule.
            current = finalLease;
            return true;
        }
        finally
        {
            // [SECURITY] Clear intermediate decrypted data (plaintext) from the pool array
            Array.Clear(tempArr, 0, decryptedSize);
            BufferLease.ByteArrayPool.Return(tempArr);
        }
    }

    /// <summary>
    /// Applies outbound transforms in transport order: compress first, then encrypt.
    /// Mutates the <paramref name="current"/> lease directly via <see langword="ref"/> to optimize performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ProcessOutbound(
        [Borrowed] ref IBufferLease current, bool enableCompress,
        int minSizeToCompress, bool enableEncrypt, ReadOnlySpan<byte> secret, uint? seq, CipherSuiteType algorithm)
    {
        ArgumentNullException.ThrowIfNull(current);

        IBufferLease original = current;

        int payloadSize = current.Length - FrameTransformer.Offset;
        bool doCompress = enableCompress && payloadSize >= minSizeToCompress;

        if (enableEncrypt && algorithm == CipherSuiteType.None)
        {
            Throw.EncryptRequestedButNoCipher();
        }

        // [SECURITY] The source frame is the plaintext of an encrypted message. Pooled arrays are
        // not scrubbed on return (see BufferLease.ByteArrayPool.Return), so ask the owner's lease
        // to clear its used range when it is finally released.
        if (enableEncrypt && original is BufferLease plaintextLease)
        {
            plaintextLease.ZeroOnDispose = true;
        }

        if (doCompress && enableEncrypt)
        {
            if (seq == null)
            {
                Throw.EncryptRequestedButNoSeq();
            }

            ProcessOutboundFused(ref current, payloadSize, secret, seq.Value, algorithm);
            return;
        }

        if (doCompress)
        {
            IBufferLease compressed = FrameCompression.CompressFrame(current);

            if (IsCompressionWorthwhile(compressed.Length - FrameTransformer.Offset, payloadSize))
            {
                current = compressed;
            }
            else
            {
                // Incompressible payload: send it raw. The receiver skips decompression too.
                compressed.Dispose();
            }
        }
        else if (enableEncrypt)
        {
            IBufferLease prev = current;
            current = FrameCipher.EncryptFrame(current, secret, seq, algorithm);

            // If we replaced a buffer that was ALREADY a replacement (intermediate),
            // we must dispose it to avoid a leak. We do NOT dispose the 'original' one.
            if (!ReferenceEquals(prev, original))
            {
                prev.Dispose();
            }
        }
    }

    private static void ProcessOutboundFused(
        [Borrowed] ref IBufferLease current,
        int payloadSize, ReadOnlySpan<byte> secret, uint seq, CipherSuiteType algorithm)
    {
        // 1. Calculate maximum required sizes
        int maxCompSize = FrameTransformer.GetMaxCompressedSize(payloadSize);
        int maxFinalSize = FrameTransformer.GetMaxCiphertextSize(algorithm, maxCompSize);

        // 2. RENT A SINGLE LEASE: capacity = Header + Final Ciphertext + Temp Compressed Data
        int totalRequiredCapacity = FrameTransformer.Offset + maxFinalSize + maxCompSize;
        BufferLease singleLease = BufferLease.Rent(totalRequiredCapacity, zeroOnDispose: false, BufferLease.TransportHeadroom);
        singleLease.IsReliable = current.IsReliable;

        try
        {
            ReadOnlySpan<byte> srcSpan = current.Span;
            Span<byte> destFull = singleLease.SpanFull;

            // 3. Slice regions from the same lease
            Span<byte> finalRegion = destFull.Slice(FrameTransformer.Offset, maxFinalSize);
            Span<byte> tempRegion = destFull.Slice(FrameTransformer.Offset + maxFinalSize, maxCompSize);

            // 4. LZ4 compress directly into tempRegion
            int compLen = LZ4.LZ4Codec.Encode(srcSpan[FrameTransformer.Offset..], tempRegion);
            bool compressedWins = IsCompressionWorthwhile(compLen, payloadSize);

            // 5. Encrypt the compressed block, or the raw payload when compression did not pay off
            //    (maxFinalSize is sized from maxCompSize >= payloadSize, so the raw payload fits too).
            ReadOnlySpan<byte> plaintext = compressedWins ? tempRegion[..compLen] : srcSpan[FrameTransformer.Offset..];
            EnvelopeCipher.Encrypt(secret, plaintext, finalRegion, null, seq, algorithm, out int encLen);

            // [SECURITY] 5.5: Clear intermediate compressed data from the memory pool
            tempRegion[..compLen].Clear();

            // 6. Copy header once and set flags
            srcSpan[..FrameTransformer.Offset].CopyTo(destFull[..FrameTransformer.Offset]);

            ref PacketHeader header = ref destFull.AsHeaderRef();
            header.Flags |= compressedWins ? PacketFlags.COMPRESSED | PacketFlags.ENCRYPTED : PacketFlags.ENCRYPTED;

            // 7. Finalize length
            singleLease.CommitLength(FrameTransformer.Offset + encLen);

            // IMPORTANT: Only swap the reference.
            // DO NOT call current.Dispose() here to preserve the original lease ownership rule.
            current = singleLease;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // [SECURITY] The temp region may hold (partially) compressed plaintext and sits past the
            // committed length, so ZeroOnDispose would not reach it. Scrub the whole region before
            // the array goes back to the pool; this is the failure path, so the cost is irrelevant.
            singleLease.SpanFull.Slice(FrameTransformer.Offset + maxFinalSize, maxCompSize).Clear();

            singleLease.Dispose();
            throw;
        }
    }
}
