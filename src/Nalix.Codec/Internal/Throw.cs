// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Exceptions;

namespace Nalix.Codec.Internal;

/// <summary>
/// Provides cached, zero-allocation exception instances for Abstractions framework errors.
/// These instances avoid the overhead of stack trace generation by overriding the StackTrace property.
/// </summary>
internal static class Throw
{
    #region Cached Exceptions (private)

    private static readonly LZ4Exception s_lz4InvalidHeader =
        new CachedLZ4Exception("The LZ4 block header is invalid or corrupt.");

    private static readonly LZ4Exception s_lz4CorruptPayload =
        new CachedLZ4Exception("The LZ4 payload is malformed or corrupt.");

    private static readonly LZ4Exception s_lz4OutputBufferTooSmall =
        new CachedLZ4Exception("The output buffer is too small for the decompressed LZ4 payload.");

    private static readonly LZ4Exception s_lz4EncoderOutputBufferTooSmall =
        new CachedLZ4Exception("LZ4 compression failed: the destination buffer is too small.");

    private static readonly SerializationFailureException s_serializationEmptyBuffer =
        new CachedSerializationException("Cannot deserialize from an empty buffer.");

    private static readonly SerializationFailureException s_serializationBufferTooSmall =
        new CachedSerializationException("The provided buffer is too small for the operation.");

    private static readonly SerializationFailureException s_serializationEndOfStream =
        new CachedSerializationException("The end of the stream was reached prematurely.");

    private static readonly SerializationFailureException s_serializationOverflow =
        new CachedSerializationException("Serialization data size overflow.");

    private static readonly SerializationFailureException s_serializationStringTooLong =
        new CachedSerializationException("The string exceeds the allowed limit.");

    private static readonly SerializationFailureException s_serializationLengthOutOfRange =
        new CachedSerializationException("The provided length is out of range.");

    private static readonly SerializationFailureException s_serializationDataMismatch =
        new CachedSerializationException("Serialization data mismatch.");

    private static readonly CipherException s_cipherOutputLengthMismatch =
        new CachedCipherException("The output length does not match the input length.");

    private static readonly CipherException s_cipherInvalidKeyLength =
        new CachedCipherException("The key length is invalid.");

    private static readonly CipherException s_cipherInvalidNonceLength =
        new CachedCipherException("The nonce length is invalid.");

    private static readonly CipherException s_cipherInvalidTagLength =
        new CachedCipherException("The authentication tag length is invalid.");

    private static readonly CipherException s_ciphertextTooShort =
        new CachedCipherException("The ciphertext buffer is too small.");

    private static readonly CipherException s_cipherAeadAuthenticationFailed =
        new CachedCipherException("AEAD authentication failed.");

    private static readonly CipherException s_cipherUnsupportedAlgorithm =
        new CachedCipherException("Unsupported cipher algorithm.");

    private static readonly InternalErrorException s_serializationFixedBufferExpansion =
        new CachedInternalErrorException("Cannot expand a fixed-size serialization buffer.");

    private static readonly ArgumentOutOfRangeException s_writerAdvanceOutOfBound =
        new CachedArgumentOutOfRangeException("count", "Advance out of buffer bounds.");

    private static readonly LZ4Exception s_transformSourceTooSmallForLZ4Header =
        new CachedLZ4Exception("The source buffer is too small to contain an LZ4 block header.");

    private static readonly LZ4Exception s_transformInvalidDecompressedLength =
        new CachedLZ4Exception("LZ4 header declares an invalid OriginalLength. Must be in range [1, MaxDecompressedSize].");

    private static readonly CipherException s_transformEncryptionKeyEmpty =
        new CachedCipherException("Encryption key cannot be null or empty.");

    private static readonly InternalErrorException s_transformSourceTooSmall =
        new CachedInternalErrorException("Source too small: buffer must be larger than the header offset.");

    private static readonly InternalErrorException s_transformDestinationTooSmall =
        new CachedInternalErrorException("Destination too small: capacity must be at least the header offset.");

    private static readonly InternalErrorException s_transformBufferTooSmallForPacket =
        new CachedInternalErrorException("The source and destination buffers must contain a packet header and be large enough for the payload.");

    private static readonly CipherException s_transformCiphertextFrameTooShort =
        new CachedCipherException("Ciphertext frame is too short to contain a valid envelope header.");

    private static readonly CipherException s_transformEncryptedButNoCipher =
        new CachedCipherException("Encrypted frame received but no cipher suite has been negotiated.");

    private static readonly CipherException s_transformEncryptedButNoKey =
        new CachedCipherException("Encrypted frame received before session key establishment.");

    private static readonly CipherException s_transformEncryptRequestedButNoCipher =
        new CachedCipherException("Encryption requested but no cipher suite has been negotiated.");

    private static readonly CipherException s_transformEncryptButNoSeq =
        new CachedCipherException("Encryption requested but no sequence number is available.");

    private static readonly CipherException s_transformInboundPipelineFailed =
        new CachedCipherException("Inbound frame pipeline processing failed due to decryption or decompression error.");

    #endregion Cached Exceptions (private)

    #region Throw Helpers — LZ4

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InvalidHeader() => throw s_lz4InvalidHeader;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CorruptPayload() => throw s_lz4CorruptPayload;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OutputBufferTooSmall() => throw s_lz4OutputBufferTooSmall;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EncoderOutputBufferTooSmall() => throw s_lz4EncoderOutputBufferTooSmall;

    #endregion Throw Helpers — LZ4

    #region Throw Helpers — Serialization

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EmptyBuffer() => throw s_serializationEmptyBuffer;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BufferTooSmall() => throw s_serializationBufferTooSmall;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EndOfStream() => throw s_serializationEndOfStream;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Overflow() => throw s_serializationOverflow;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void StringTooLong() => throw s_serializationStringTooLong;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void LengthOutOfRange() => throw s_serializationLengthOutOfRange;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void DataMismatch() => throw s_serializationDataMismatch;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SerializationFailed(Type type, string message) =>
        throw new SerializationFailureException($"Serialization failed for type '{type.FullName}': {message}");

    #endregion Throw Helpers — Serialization

    #region Throw Helpers — Security

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OutputLengthMismatch() => throw s_cipherOutputLengthMismatch;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InvalidKeyLength() => throw s_cipherInvalidKeyLength;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InvalidNonceLength() => throw s_cipherInvalidNonceLength;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InvalidTagLength() => throw s_cipherInvalidTagLength;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CiphertextBufferTooShort() => throw s_ciphertextTooShort;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AeadAuthenticationFailed() => throw s_cipherAeadAuthenticationFailed;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UnsupportedAlgorithm() => throw s_cipherUnsupportedAlgorithm;

    #endregion Throw Helpers — Security

    #region Throw Helpers — Resource

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void FixedBufferExpansion() => throw s_serializationFixedBufferExpansion;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AdvanceOutOfBound() => throw s_writerAdvanceOutOfBound;

    #endregion Throw Helpers — Resource

    #region Throw Helpers — Transform

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SourceTooSmallForLZ4Header() => throw s_transformSourceTooSmallForLZ4Header;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InvalidDecompressedLength() => throw s_transformInvalidDecompressedLength;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EncryptionKeyEmpty() => throw s_transformEncryptionKeyEmpty;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SourceTooSmall() => throw s_transformSourceTooSmall;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void DestinationTooSmall() => throw s_transformDestinationTooSmall;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void BufferTooSmallForPacket() => throw s_transformBufferTooSmallForPacket;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void CiphertextFrameTooShort() => throw s_transformCiphertextFrameTooShort;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EncryptedButNoCipher() => throw s_transformEncryptedButNoCipher;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EncryptedButNoKey() => throw s_transformEncryptedButNoKey;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EncryptRequestedButNoCipher() => throw s_transformEncryptRequestedButNoCipher;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EncryptRequestedButNoSeq() => throw s_transformEncryptButNoSeq;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InboundPipelineFailed() => throw s_transformInboundPipelineFailed;

    #endregion Throw Helpers — Transform

    #region Private Cached Exception Types

    [StackTraceHidden]
    private sealed class CachedSerializationException(string message) : SerializationFailureException(message)
    {
        public override string StackTrace => "   at Nalix.Codec.Serialization (Cached Exception)";
    }

    [StackTraceHidden]
    private sealed class CachedCipherException(string message) : CipherException(message)
    {
        public override string StackTrace => "   at Nalix.Codec.Security (Cached Exception)";
    }

    [StackTraceHidden]
    private sealed class CachedInternalErrorException(string message) : InternalErrorException(message)
    {
        public override string StackTrace => "   at Nalix.Codec (Cached Exception)";
    }

    [StackTraceHidden]
    private sealed class CachedLZ4Exception(string message) : LZ4Exception(message)
    {
        public override string StackTrace => "   at Nalix.Codec.LZ4 (Cached Exception)";
    }

    [StackTraceHidden]
    private sealed class CachedArgumentOutOfRangeException(string paramName, string message) : ArgumentOutOfRangeException(paramName, message)
    {
        public override string StackTrace => "   at Nalix.Environment.Memory (Cached Exception)";
    }

    #endregion Private Cached Exception Types
}
