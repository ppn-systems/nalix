// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nalix.Codec.Internal.Memory;
using Nalix.Codec.LZ4;

#if DEBUG
using System.Diagnostics;

#endif

namespace Nalix.Codec.Internal.LZ4;

/// <summary>
/// Provides functionality for finding matches in input data using a hash table.
/// Optimized for high performance in LZ4 compression.
/// </summary>
[SkipLocalsInit]
[EditorBrowsable(EditorBrowsableState.Never)]
internal static unsafe class MatchFinder
{
    // Max constants for bounds checking if needed, otherwise removed.
    // Hash sizes are now dynamically calculated per payload to avoid excessive memory clearing.

    #region Constructors

    /// <summary>
    /// Represents a found match with an offset and length.
    /// </summary>
    /// <param name="offset">The match offset relative to the current position.</param>
    /// <param name="length">The match length.</param>
    /// <remarks>
    /// Initializes a new instance of the <see cref="Match"/> struct.
    /// </remarks>
    internal readonly struct Match(int offset, int length)
    {
        /// <summary>
        /// Match offset relative to current position
        /// </summary>
        public readonly int Offset = offset;

        /// <summary>
        /// Length of the match
        /// </summary>
        public readonly int Length = length;

        /// <summary>
        /// Determines if the match is valid.
        /// A match is considered valid if its length meets the minimum match length.
        /// </summary>
        public bool Found => Length >= LZ4CompressionConstants.MinMatchLength;
    }

    #endregion Constructors

    #region Methods

    /// <summary>
    /// Finds the longest match for the current input position within the sliding window.
    /// </summary>
    /// <param name="hashTable">Hash table mapping hash values to input offsets.</param>
    /// <param name="hashShift">Hash</param>
    /// <param name="hashMask">Hash</param>
    /// <param name="inputBase">Pointer to the start of the input buffer.</param>
    /// <param name="currentInputPtr">Pointer to the current position in the input buffer.</param>
    /// <param name="inputLimit">Pointer to the end of the input buffer or match limit.</param>
    /// <param name="searchStartPtr">Pointer to the start of the sliding window for searching.</param>
    /// <param name="currentInputOffset">Offset of the current input pointer relative to the input base.</param>
    /// <returns>A <see cref="Match"/> struct representing the longest match found, or a default value if no match is found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Match FindLongestMatch(
        int* hashTable,
        int hashShift,
        int hashMask,
        byte* inputBase,
        byte* currentInputPtr,
        byte* inputLimit,
        byte* searchStartPtr,
        int currentInputOffset)
    {
#if DEBUG
        Debug.Assert(inputBase is not null, "Input base pointer is null");
        Debug.Assert(hashTable is not null, "Hash table pointer is null");
        Debug.Assert(currentInputPtr is not null, "Current input pointer is null");
        Debug.Assert(currentInputPtr >= inputBase, "Current pointer is before base");
        Debug.Assert(inputLimit >= currentInputPtr, "Input limit is before current pointer");
#endif

        // Ensure there are enough bytes to find a match
        if ((nuint)(inputLimit - currentInputPtr) < LZ4CompressionConstants.MinMatchLength)
        {
            return default;
        }

        // ✅ FIX: Protect against reading beyond buffer
        if (currentInputPtr + sizeof(uint) > inputLimit + LZ4CompressionConstants.LastLiteralSize)
        {
            return default;
        }

        // Read the current 4-byte sequence and compute its hash
        uint currentSequence = MemOps.ReadUnaligned<uint>(currentInputPtr);
        uint hash;

#if NET5_0_OR_GREATER
        if (System.Runtime.Intrinsics.X86.Sse42.IsSupported)
        {
            hash = System.Runtime.Intrinsics.X86.Sse42.Crc32(0, currentSequence);
        }
        else
#endif
        {
            hash = (currentSequence * 2654435761u) >> hashShift;
        }

        hash &= (uint)hashMask;

        // Retrieve the candidate match offset and update the hash table
        int matchCandidateOffset = hashTable[hash];
        hashTable[hash] = currentInputOffset;

        if (matchCandidateOffset < 0 || matchCandidateOffset >= currentInputOffset)
        {
            return default;
        }

        // Calculate the candidate match pointer
        byte* matchCandidatePtr = inputBase + matchCandidateOffset;

        if (matchCandidatePtr < searchStartPtr)
        {
            return default;
        }

        // Calculate offset first
        int offset = (int)(currentInputPtr - matchCandidatePtr);

        if (offset is <= 0 or > LZ4CompressionConstants.MaxOffset)
        {
            return default;
        }

        // Check if sequences match
        if (MemOps.ReadUnaligned<uint>(matchCandidatePtr) != currentSequence)
        {
            return default;
        }

        // Calculate the length of the match
        byte* matchEnd = matchCandidatePtr + LZ4CompressionConstants.MinMatchLength;
        byte* currentEnd = currentInputPtr + LZ4CompressionConstants.MinMatchLength;

        int maxMatchLen = (int)(inputLimit - currentEnd);
        if (maxMatchLen < 0)
        {
            maxMatchLen = 0;
        }

        int additionalMatchBytes = MemOps.CountEqualBytes(matchEnd, currentEnd, maxMatchLen);
        int matchLength = LZ4CompressionConstants.MinMatchLength + additionalMatchBytes;

        return new Match(offset, matchLength);
    }

    #endregion Methods
}
