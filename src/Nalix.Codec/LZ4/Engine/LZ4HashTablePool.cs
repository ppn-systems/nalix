// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.ComponentModel;

namespace Nalix.Codec.LZ4.Engine;

/// <summary>
/// Provides pooled hash tables for LZ4 compression to avoid repeated allocations.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class LZ4HashTablePool
{
    #region Fields

    [ThreadStatic]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "<Pending>")]
    private static int[]? t_hashTable;
    private static readonly ArrayPool<int> s_pool = ArrayPool<int>.Shared;

    #endregion Fields

    #region APIs

    /// <summary>
    /// Gets a thread-local hash table for compression operations.
    /// </summary>
    /// <remarks>
    /// A reused thread-local table is <b>not</b> cleared. Its entries are only match hints (input
    /// offsets): <c>MatchFinder.FindLongestMatch</c> rejects any candidate that is negative, not
    /// before the current position, outside the 64 KB window, or whose 4 bytes differ from the
    /// current sequence. A stale entry left by a previous block therefore behaves exactly like a
    /// hash collision: it can only cost a missed match, never a wrong one, and it cannot expose data
    /// from the previous block because the table stores offsets, not bytes. Skipping the clear saves
    /// up to 256 KB of memset per compressed frame. Freshly pooled tables are still cleared once.
    /// </remarks>
    /// <returns>A hash table ready for use.</returns>
    public static int[] Rent(int hashBits)
    {
        int size = 1 << hashBits;
        int[]? hashTable = t_hashTable;

        if (hashTable is not null && hashTable.Length >= size)
        {
            t_hashTable = null;
            return hashTable;
        }

        hashTable = s_pool.Rent(size);
        new Span<int>(hashTable, 0, size).Clear();
        return hashTable;
    }

    /// <summary>
    /// Returns a hash table to the pool (no-op for thread-static storage).
    /// </summary>
    /// <param name="hashTable"></param>
    public static void Return(int[] hashTable)
    {
        if (hashTable is null)
        {
            return;
        }

        // Ưu tiên giữ lại mảng có dung lượng lớn nhất cho thread hiện tại
        if (t_hashTable is null || hashTable.Length > t_hashTable.Length)
        {
            if (t_hashTable is not null)
            {
                s_pool.Return(t_hashTable);
            }
            t_hashTable = hashTable;
            return;
        }

        // Trả về pool nếu không dùng thread-local
        s_pool.Return(hashTable);
    }

    /// <summary>
    /// Clears the thread-local hash table cache (useful for testing or memory cleanup).
    /// </summary>
    public static void Clear()
    {
        if (t_hashTable is not null)
        {
            s_pool.Return(t_hashTable);
            t_hashTable = null;
        }
    }

    #endregion APIs
}
