// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Internal;
using Nalix.Codec.Internal.Memory;
using Nalix.Codec.Security.Primitives;
using Nalix.Environment.Memory;

namespace Nalix.Codec.LZ4.Engine;

/// <summary>
/// Provides decompression functionality for the LZ4 format.
/// </summary>
[SkipLocalsInit]
[DebuggerNonUserCode]
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class LZ4Decoder
{
    public const int ErrorInvalidHeader = -1;
    public const int ErrorOutputBufferTooSmall = -2;
    public const int ErrorCorruptPayload = -3;

    #region APIs

    /// <summary>
    /// Decompresses the provided compressed data into the specified output buffer.
    /// </summary>
    /// <param name="input">The compressed data, including the header.</param>
    /// <param name="output">The buffer to store decompressed data. Size must match the original length in the header.</param>
    /// <returns>The number of bytes written to the output buffer (equal to the original length).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int result = TryDecode(input, output);
        if (result < 0)
        {
            ThrowDecodeError(result);
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int TryDecode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int headerStatus = TryReadAndValidateHeader(input, out LZ4BlockHeader header);
        if (headerStatus < 0)
        {
            return headerStatus;
        }
        return DecodeInternal(input, output, header);
    }

    /// <summary>
    /// Decompresses the provided compressed data into a <see cref="BufferLease"/> rented from the pool.
    /// Caller is responsible for disposing the lease when done.
    /// </summary>
    /// <param name="input">The compressed data, including the header.</param>
    /// <param name="lease">
    /// On success, a <see cref="BufferLease"/> whose <see cref="BufferLease.Span"/> contains
    /// exactly <c>bytesWritten</c> bytes of decompressed data. Must be disposed by the caller.
    /// This method throws on invalid compressed input or internal decode failure.
    /// </param>
    /// <param name="bytesWritten">The number of bytes written to the lease.</param>
    /// <returns><c>true</c> when decompression completes successfully.</returns>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Decode(ReadOnlySpan<byte> input, out BufferLease? lease, out int bytesWritten)
    {
        int result = TryDecode(input, out lease, out bytesWritten);
        if (result < 0)
        {
            ThrowDecodeError(result);
        }
        return true;
    }

    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int TryDecode(ReadOnlySpan<byte> input, out BufferLease? lease, out int bytesWritten)
    {
        lease = null;
        bytesWritten = 0;

        int headerStatus = TryReadAndValidateHeader(input, out LZ4BlockHeader header);
        if (headerStatus < 0)
        {
            return headerStatus;
        }

        if (header.OriginalLength == 0)
        {
            return 0;
        }

        BufferLease rentedLease = BufferLease.Rent(header.OriginalLength);
        try
        {
            int result = DecodeInternal(input, rentedLease.SpanFull, header);
            if (result < 0)
            {
                rentedLease.Dispose();
                return result;
            }
            bytesWritten = result;
            rentedLease.CommitLength(bytesWritten);
            lease = rentedLease;
            return result;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            rentedLease.Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDecodeError(int errorCode)
    {
        switch (errorCode)
        {
            case ErrorInvalidHeader:
                Throw.InvalidHeader();
                break;
            case ErrorOutputBufferTooSmall:
                Throw.OutputBufferTooSmall();
                break;
            case ErrorCorruptPayload:
                Throw.CorruptPayload();
                break;
            default:
                break;
        }
    }

    #endregion APIs

    #region Private Methods

    /// <summary>
    /// Validates input length and reads the LZ4 block header.
    /// Centralises header checks shared by all three Decode overloads and FrameTransformer.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="header"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int TryReadAndValidateHeader(ReadOnlySpan<byte> input, out LZ4BlockHeader header)
    {
        header = default;
        if (input.Length < LZ4BlockHeader.Size)
        {
            return ErrorInvalidHeader;
        }

        header = MemOps.ReadUnaligned<LZ4BlockHeader>(input);

        if (header.OriginalLength < 0 || header.OriginalLength > LZ4CompressionConstants.MaxBlockSize)
        {
            return ErrorInvalidHeader;
        }

        if (header.CompressedLength < LZ4BlockHeader.Size || header.CompressedLength != input.Length)
        {
            return ErrorInvalidHeader;
        }

        int payloadLength = input.Length - LZ4BlockHeader.Size;
        if (header.OriginalLength == 0)
        {
            return payloadLength == 0 ? 0 : ErrorInvalidHeader;
        }

        // Mathematical upper bound on LZ4 block expansion:
        // - Every non-empty LZ4 block must start with at least 1 token byte + 1 literal byte (payloadLength >= 2).
        // - Blocks with payloadLength < 4 cannot fit a 2-byte match offset after the initial literal, so they
        //   can only emit literals: OriginalLength <= payloadLength - 1.
        // - Blocks with payloadLength >= 4 can at best spend 4 bytes on (token + 1 literal + 2-byte offset) for
        //   19 output bytes, and every additional input byte as a 0xFF match-length varint byte (+255 bytes).
        long maxPossibleOriginalLength = payloadLength < 4
            ? payloadLength - 1
            : ((long)(payloadLength - 4) * 255) + 19;

        if (header.OriginalLength > maxPossibleOriginalLength)
        {
            return ErrorInvalidHeader;
        }

        return 0;
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int DecodeInternal(ReadOnlySpan<byte> input, Span<byte> output, in LZ4BlockHeader header)
    {
        if (header.OriginalLength > output.Length)
        {
            return ErrorOutputBufferTooSmall;
        }

        if (header.OriginalLength == 0)
        {
            return 0;
        }

        fixed (byte* inputBase = &MemoryMarshal.GetReference(input))
        {
            fixed (byte* outputBase = &MemoryMarshal.GetReference(output))
            {
                byte* inputPtr = inputBase + LZ4BlockHeader.Size;
                byte* inputEnd = inputBase + header.CompressedLength;
                byte* outputPtr = outputBase;
                byte* outputEnd = outputBase + header.OriginalLength;

                while (inputPtr < inputEnd)
                {
                    byte token = *inputPtr++;

                    int literalLength = (token >> 4) & LZ4CompressionConstants.TokenLiteralMask;

                    if (literalLength == LZ4CompressionConstants.TokenLiteralMask)
                    {
                        int bytesRead = SpanOps.ReadVarInt(ref inputPtr, inputEnd, out int extraLength);
                        if (bytesRead == -1 || extraLength < 0 || extraLength > (int)(inputEnd - inputPtr) - literalLength)
                        {
                            MemorySecurity.ZeroMemory(output[..(int)(outputPtr - outputBase)]);
                            return ErrorCorruptPayload;
                        }

                        literalLength += extraLength;
                    }

                    if (literalLength > 0)
                    {
                        if (literalLength > (int)(inputEnd - inputPtr) || literalLength > (int)(outputEnd - outputPtr))
                        {
                            MemorySecurity.ZeroMemory(output[..(int)(outputPtr - outputBase)]);
                            return ErrorCorruptPayload;
                        }

                        MemOps.Copy(inputPtr, outputPtr, literalLength);
                        inputPtr += literalLength;
                        outputPtr += literalLength;
                    }

                    if (inputPtr >= inputEnd || outputPtr >= outputEnd)
                    {
                        break;
                    }

                    if ((inputEnd - inputPtr) < sizeof(ushort))
                    {
                        MemorySecurity.ZeroMemory(output[..(int)(outputPtr - outputBase)]);
                        return ErrorCorruptPayload;
                    }

                    int offset = MemOps.ReadUnaligned<ushort>(inputPtr);
                    inputPtr += sizeof(ushort);
                    if (offset == 0 || offset > (outputPtr - outputBase))
                    {
                        MemorySecurity.ZeroMemory(output[..(int)(outputPtr - outputBase)]);
                        return ErrorCorruptPayload;
                    }

                    int remainingOutput = (int)(outputEnd - outputPtr);
                    int matchLength = token & LZ4CompressionConstants.TokenMatchMask;
                    if (matchLength == LZ4CompressionConstants.TokenMatchMask)
                    {
                        int bytesRead = SpanOps.ReadVarInt(ref inputPtr, inputEnd, out int extraLength);
                        if (bytesRead == -1 || extraLength < 0 || extraLength > remainingOutput - (LZ4CompressionConstants.TokenMatchMask + LZ4CompressionConstants.MinMatchLength))
                        {
                            MemorySecurity.ZeroMemory(output[..(int)(outputPtr - outputBase)]);
                            return ErrorCorruptPayload;
                        }

                        matchLength += extraLength;
                    }
                    matchLength += LZ4CompressionConstants.MinMatchLength;

                    if (matchLength > remainingOutput)
                    {
                        MemorySecurity.ZeroMemory(output[..(int)(outputPtr - outputBase)]);
                        return ErrorCorruptPayload;
                    }

                    byte* matchSourcePtr = outputPtr - offset;
                    MemOps.Copy(matchSourcePtr, outputPtr, matchLength);
                    outputPtr += matchLength;
                }

                if (outputPtr != outputEnd || inputPtr != inputEnd)
                {
                    MemorySecurity.ZeroMemory(output[..(int)(outputPtr - outputBase)]);
                    return ErrorCorruptPayload;
                }

                return header.OriginalLength;
            }
        }
    }

    #endregion Private Methods
}
