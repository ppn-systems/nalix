// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nalix.Codec.Security.Primitives;

/// <summary>
/// Provides helper methods for securely clearing sensitive data from memory.
/// This type is intended for security-critical paths such as credential handling,
/// key derivation, or symmetric encryption where sensitive information must be removed
/// from memory immediately after usage.
/// </summary>
[DebuggerNonUserCode]
public static class MemorySecurity
{
    /// <summary>
    /// Overwrites the provided <paramref name="buffer"/> with zeros.
    /// This method does not throw if the buffer is <c>null</c> or empty.
    /// </summary>
    /// <param name="buffer">
    /// The byte array containing sensitive information that should be cleared.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ZeroMemory(byte[] buffer)
    {
        if (buffer is null || buffer.Length == 0)
        {
            return;
        }

        ZeroMemory(MemoryExtensions.AsSpan(buffer));

        GC.KeepAlive(buffer);
    }

    /// <summary>
    /// Overwrites the provided <paramref name="buffer"/> span with zeros.
    /// </summary>
    /// <param name="buffer">
    /// The span representing sensitive byte data that should be cleared.
    /// </param>
    // Same shape as CryptographicOperations.ZeroMemory (which NALIX071 keeps out of the core assemblies): a call
    // the JIT can neither inline nor optimize, so the stores are never elided, around the vectorized Span.Clear.
    // The byte-by-byte Volatile.Write loop it replaces was a large share of short AEAD calls.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static void ZeroMemory(Span<byte> buffer) => buffer.Clear();
}
