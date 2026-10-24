// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

#pragma warning disable IDE0079
#pragma warning disable CA1859

using System;
using System.Runtime.CompilerServices;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Security;
using Nalix.Codec.Internal;
using Nalix.Codec.LZ4;
using Nalix.Codec.LZ4.Engine;
using Nalix.Codec.Options;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Internal;
using Nalix.Environment.Configuration;

namespace Nalix.Codec.Transforms;

/// <summary>
/// Provides high-performance packet transformation utilities including encryption,
/// decryption, compression, and decompression.
/// </summary>
/// <remarks>
/// This class operates only on the payload region (<c>DATA_REGION</c>) of a packet,
/// preserving the header section unchanged.
/// <para>
/// Designed for low-level networking scenarios with zero-allocation patterns using <see cref="Span{T}"/>.
/// </para>
/// </remarks>
public static class FrameTransformer
{
    /// <summary>
    /// Offset in bytes where the payload (DATA_REGION) starts in the packet.
    /// </summary>
    public const int Offset = (int)PacketHeaderOffset.Region;

    private static readonly CompressionOptions s_compressionOptions = ConfigurationManager.Instance.Get<CompressionOptions>();

    /// <summary>
    /// Calculates the maximum ciphertext size required for encrypting a plaintext of the given size
    /// with the specified cipher suite type.
    /// </summary>
    /// <param name="type">The cipher suite type.</param>
    /// <param name="plaintextSize">Size of the plaintext input in bytes.</param>
    /// <returns>
    /// Maximum bytes required for the ciphertext buffer, i.e., encrypted envelope size.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="type"/> is not a supported cipher suite.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMaxCiphertextSize(CipherSuiteType type, int plaintextSize)
    {
        checked
        {
            int tagSize = EnvelopeCipher.GetTagLength(type);
            int nonceSize = EnvelopeCipher.GetNonceLength(type);

            // Total envelope size: header + nonce + ciphertext + tag (if any)
            return EnvelopeFormat.HeaderSize + nonceSize + plaintextSize + tagSize;
        }
    }

    /// <summary>
    /// Returns the size of plaintext from an encrypted envelope (header || nonce || ciphertext [|| tag]).
    /// </summary>
    /// <param name="envelope">The input envelope.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="envelope"/> does not contain a valid Nalix packet envelope.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPlaintextLength(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < Offset)
        {
            Throw.SourceTooSmall();
        }

        EnvelopeFormat.Envelope parsed = EnvelopeFormat.ParseEnvelope(envelope[Offset..]);

        return parsed.Ciphertext.Length;
    }

    /// <summary>
    /// Attempts to get the plaintext length from an encrypted envelope without throwing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetPlaintextLength(ReadOnlySpan<byte> envelope, out int length)
    {
        length = 0;
        if (envelope.Length <= Offset)
        {
            return false;
        }

        if (!EnvelopeFormat.TryParseEnvelope(envelope[Offset..], out EnvelopeFormat.Envelope parsed, out _))
        {
            return false;
        }

        length = parsed.Ciphertext.Length;
        return true;
    }

    /// <summary>
    /// Calculates the maximum compressed size for a given plaintext size using LZ4 compression.
    /// </summary>
    /// <param name="plaintextSize">Size of the plaintext input in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="plaintextSize"/> is negative.</exception>
    public static int GetMaxCompressedSize(int plaintextSize) => LZ4BlockEncoder.GetMaxLength(plaintextSize);

    /// <summary>
    /// Reads the original uncompressed payload length from an LZ4 block header.
    /// </summary>
    /// <param name="src">The source packet buffer containing an LZ4-compressed payload.</param>
    /// <returns>The original payload length stored in the LZ4 block header.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="src"/> is shorter than an <see cref="LZ4BlockHeader"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDecompressedLength(ReadOnlySpan<byte> src)
    {
        if (src.Length < Unsafe.SizeOf<LZ4BlockHeader>())
        {
            Throw.SourceTooSmallForLZ4Header();
        }

        if (!TryGetDecompressedLength(src, out int length))
        {
            Throw.InvalidDecompressedLength();
        }

        return length;
    }

    /// <summary>
    /// Attempts to get the uncompressed payload length from an LZ4 block header without throwing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetDecompressedLength(ReadOnlySpan<byte> src, out int length)
    {
        length = 0;
        if (LZ4Decoder.TryReadAndValidateHeader(src, out LZ4BlockHeader header) < 0)
        {
            return false;
        }

        // Validate OriginalLength against both configured MaxDecompressedSize and hard LZ4 MaxBlockSize.
        int limit = Math.Min(s_compressionOptions.MaxDecompressedSize, LZ4CompressionConstants.MaxBlockSize);

        if (header.OriginalLength <= 0 || header.OriginalLength > limit)
        {
            return false;
        }

        length = header.OriginalLength;
        return true;
    }

    /// <summary>
    /// Encrypts the payload (DATA_REGION) of a packet while preserving the header.
    /// </summary>
    /// <param name="src">The source buffer containing the original packet.</param>
    /// <param name="dest">The destination buffer to write the encrypted packet.</param>
    /// <param name="key">The encryption key.</param>
    /// <param name="seq">>The sequence number send in the encrypted envelope.</param>
    /// <param name="suite">The cipher suite used for encryption.</param>
    /// <remarks>
    /// The header portion of the packet is copied unchanged.
    /// Only the payload is encrypted.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="src"/> or <paramref name="dest"/> is null, or when <paramref name="key"/> is empty.</exception>
    /// <exception cref="ArgumentException">Thrown when the source or destination buffer is too small.</exception>
    /// <exception cref="CipherException">
    /// Thrown when the selected cipher rejects the supplied key or destination envelope.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Encrypt([Borrowed] IBufferLease src, [Borrowed] IBufferLease dest, ReadOnlySpan<byte> key, uint? seq, CipherSuiteType suite)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dest);

        if (key.IsEmpty)
        {
            Throw.EncryptionKeyEmpty();
        }

        if (src.Length < Offset)
        {
            Throw.SourceTooSmall();
        }

        if (dest.Capacity < Offset)
        {
            Throw.DestinationTooSmall();
        }

        Span<byte> destFull = dest.SpanFull;
        ReadOnlySpan<byte> srcSpan = src.Span;

        srcSpan[..Offset].CopyTo(destFull[..Offset]);

        Span<byte> aad = stackalloc byte[Offset];
        WriteHeaderAad(srcSpan, aad);

        EnvelopeCipher.Encrypt(key, srcSpan[Offset..], destFull[Offset..], aad, seq, suite, out int encrypted);
        dest.CommitLength(Offset + encrypted);
    }

    /// <summary>
    /// Decrypts the payload (DATA_REGION) of a packet while preserving the header.
    /// </summary>
    /// <param name="src">The source buffer containing the encrypted packet.</param>
    /// <param name="dest">The destination buffer to write the decrypted packet.</param>
    /// <param name="key">The decryption key.</param>
    /// <param name="expectedAlgorithm">The expected cipher suite to validate/decrypt the envelope.</param>
    /// <param name="seq">The sequence number received in the decrypted envelope.</param>
    /// <remarks>
    /// The header portion is copied unchanged.
    /// Only the payload is decrypted.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="src"/> or <paramref name="dest"/> is null, or when <paramref name="key"/> is empty.</exception>
    /// <exception cref="ArgumentException">Thrown when the source or destination buffer is too small.</exception>
    /// <exception cref="CipherException">Thrown when AEAD authentication fails during payload decryption.</exception>
    /// <exception cref="NotSupportedException">Thrown when the encrypted payload declares an unsupported cipher suite.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decrypt(
        [Borrowed] IBufferLease src,
        [Borrowed] IBufferLease dest, ReadOnlySpan<byte> key, CipherSuiteType expectedAlgorithm, out uint seq)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dest);

        if (key.IsEmpty)
        {
            Throw.EncryptionKeyEmpty();
        }

        if (src.Length < Offset)
        {
            Throw.SourceTooSmall();
        }

        if (dest.Capacity < Offset)
        {
            Throw.DestinationTooSmall();
        }

        Span<byte> destFull = dest.SpanFull;
        ReadOnlySpan<byte> srcSpan = src.Span;

        srcSpan[..Offset].CopyTo(destFull[..Offset]);

        Span<byte> aad = stackalloc byte[Offset];
        WriteHeaderAad(srcSpan, aad);

        EnvelopeCipher.Decrypt(key, srcSpan[Offset..], destFull[Offset..], aad, expectedAlgorithm, out int decrypted, out seq);
        dest.CommitLength(Offset + decrypted);
    }

    /// <summary>
    /// Attempts to decrypt the payload (DATA_REGION) of a packet without throwing exceptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDecrypt(
        [Borrowed] IBufferLease src,
        [Borrowed] IBufferLease dest, ReadOnlySpan<byte> key, CipherSuiteType expectedAlgorithm, out uint seq)
    {
        seq = 0;
        if (src is null || dest is null || key.IsEmpty)
        {
            return false;
        }

        if (src.Length <= Offset || dest.Capacity < Offset)
        {
            return false;
        }

        Span<byte> destFull = dest.SpanFull;
        ReadOnlySpan<byte> srcSpan = src.Span;

        Span<byte> aad = stackalloc byte[Offset];
        WriteHeaderAad(srcSpan, aad);

        if (!EnvelopeCipher.TryDecrypt(key, srcSpan[Offset..], destFull[Offset..], aad, expectedAlgorithm, out int decrypted, out seq, out _))
        {
            return false;
        }

        srcSpan[..Offset].CopyTo(destFull[..Offset]);
        dest.CommitLength(Offset + decrypted);
        return true;
    }

    /// <summary>
    /// Writes the AEAD associated data for a frame: the <see cref="Offset"/>-byte packet header
    /// (OpCode, Flags, Priority, SequenceId) exactly as it appears on the wire, so any header
    /// modification makes authentication fail.
    /// </summary>
    /// <remarks>
    /// The <see cref="PacketFlags.ENCRYPTED"/> bit is forced to 1. Every encrypted frame carries
    /// it on the wire, but it is transform-internal bookkeeping: <see cref="FrameCipher"/> sets it
    /// only after <see cref="Encrypt"/> returns and clears it after decryption, and direct callers
    /// of <see cref="Encrypt"/>/<see cref="Decrypt"/> may not have it set at all. Fixing it to 1 keeps
    /// sender and receiver byte-identical regardless of when the bit is toggled. All other bits,
    /// including <see cref="PacketFlags.COMPRESSED"/>, must already be in their on-wire state.
    /// </remarks>
    /// <param name="frame">The frame whose first <see cref="Offset"/> bytes are the packet header.</param>
    /// <param name="aad">Destination of exactly <see cref="Offset"/> bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteHeaderAad(ReadOnlySpan<byte> frame, Span<byte> aad)
    {
        frame[..Offset].CopyTo(aad);
        aad[(int)PacketHeaderOffset.Flags] |= (byte)PacketFlags.ENCRYPTED;
    }

    /// <summary>
    /// Compresses the payload (DATA_REGION) of a packet using LZ4 while preserving the header.
    /// </summary>
    /// <param name="src">The source buffer containing the original packet.</param>
    /// <param name="dest">The destination buffer to write the compressed packet.</param>
    /// <remarks>
    /// The destination buffer must be large enough to hold the maximum possible compressed data.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="src"/> or <paramref name="dest"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the packet header or payload buffers are too small.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Compress([Borrowed] IBufferLease src, [Borrowed] IBufferLease dest)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dest);

        if (src.Length <= Offset || dest.Capacity <= Offset)
        {
            Throw.BufferTooSmallForPacket();
        }

        Span<byte> destFull = dest.SpanFull;
        ReadOnlySpan<byte> srcSpan = src.Span;

        srcSpan[..Offset].CopyTo(destFull[..Offset]);

        int compressed = LZ4Codec.Encode(srcSpan[Offset..], destFull[Offset..]);
        dest.CommitLength(Offset + compressed);
    }

    /// <summary>
    /// Decompresses the payload (DATA_REGION) of a packet using LZ4 while preserving the header.
    /// </summary>
    /// <param name="src">The source buffer containing the compressed packet.</param>
    /// <param name="dest">The destination buffer to write the decompressed packet.</param>
    /// <remarks>
    /// The destination buffer must be large enough to hold the original uncompressed payload.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="src"/> or <paramref name="dest"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the packet header or payload buffers are too small.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decompress([Borrowed] IBufferLease src, [Borrowed] IBufferLease dest)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dest);

        if (src.Length <= Offset || dest.Capacity <= Offset)
        {
            Throw.BufferTooSmallForPacket();
        }

        Span<byte> destFull = dest.SpanFull;
        ReadOnlySpan<byte> srcSpan = src.Span;

        int decoded = LZ4Codec.Decode(srcSpan[Offset..], destFull[Offset..]);
        srcSpan[..Offset].CopyTo(destFull[..Offset]);
        dest.CommitLength(Offset + decoded);
    }

    /// <summary>
    /// Attempts to decompress the payload (DATA_REGION) of a packet using LZ4 without throwing exceptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDecompress([Borrowed] IBufferLease src, [Borrowed] IBufferLease dest)
    {
        if (src is null || dest is null)
        {
            return false;
        }

        if (src.Length <= Offset || dest.Capacity <= Offset)
        {
            return false;
        }

        Span<byte> destFull = dest.SpanFull;
        ReadOnlySpan<byte> srcSpan = src.Span;

        if (!LZ4Codec.TryDecode(srcSpan[Offset..], destFull[Offset..], out int decoded))
        {
            return false;
        }

        srcSpan[..Offset].CopyTo(destFull[..Offset]);
        dest.CommitLength(Offset + decoded);
        return true;
    }
}
