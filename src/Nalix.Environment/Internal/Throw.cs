// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Exceptions;

namespace Nalix.Environment.Internal;

/// <summary>
/// Provides cached exception instances for Environment memory primitives.
/// </summary>
internal static class Throw
{
    private static readonly SerializationFailureException s_endOfStream =
        new CachedSerializationException("The end of the stream was reached prematurely.");

    private static readonly InternalErrorException s_fixedBufferExpansion =
        new CachedInternalErrorException("Cannot expand a fixed-size serialization buffer.");

    private static readonly ArgumentOutOfRangeException s_advanceOutOfBound =
        new CachedArgumentOutOfRangeException("count", "Advance out of buffer bounds.");

    private static readonly FormatException s_overlongLeb128 =
        new("LEB128 sequence is overlong or corrupt (exceeds 5 bytes for a 32-bit value).");

    private static readonly ArgumentException s_leb128BufferTooSmall =
        new("Destination buffer is too small to encode the LEB128 value.", "buffer");

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EndOfStream() => throw s_endOfStream;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void FixedBufferExpansion() => throw s_fixedBufferExpansion;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AdvanceOutOfBound() => throw s_advanceOutOfBound;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void OverlongLeb128() => throw s_overlongLeb128;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Leb128BufferTooSmall() => throw s_leb128BufferTooSmall;

    [StackTraceHidden]
    private sealed class CachedSerializationException(string message) : SerializationFailureException(message)
    {
        public override string StackTrace => "   at Nalix.Environment.Memory (Cached Exception)";
    }

    [StackTraceHidden]
    private sealed class CachedInternalErrorException(string message) : InternalErrorException(message)
    {
        public override string StackTrace => "   at Nalix.Environment.Memory (Cached Exception)";
    }

    [StackTraceHidden]
    private sealed class CachedArgumentOutOfRangeException(string paramName, string message) : ArgumentOutOfRangeException(paramName, message)
    {
        public override string StackTrace => "   at Nalix.Environment.Memory (Cached Exception)";
    }
}
