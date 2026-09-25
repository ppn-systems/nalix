// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif
using Nalix.Abstractions.Serialization;

namespace Nalix.Abstractions.Primitives;

/// <summary>
/// Represents a fixed-size 256-bit buffer (32 bytes).
/// Used for storing cryptographic hashes, proofs, or signatures.
/// </summary>
#if NET8_0_OR_GREATER
[SkipLocalsInit]
#endif
[DebuggerDisplay("{ToString()}")]
[StructLayout(LayoutKind.Explicit)]
[SerializePackable(SerializeLayout.Explicit)]
public readonly struct Bytes32 : IEquatable<Bytes32>, IFixedSizeSerializable
{
    /// <summary>
    /// The size of the <see cref="Bytes32"/> buffer in bytes.
    /// </summary>
    public const int Size = 0x20;

    static int IFixedSizeSerializable.Size => Size;

    [FieldOffset(0x00)][SerializeOrder(0)] private readonly ulong _v1;
    [FieldOffset(0x08)][SerializeOrder(1)] private readonly ulong _v2;
    [FieldOffset(0x10)][SerializeOrder(2)] private readonly ulong _v3;
    [FieldOffset(0x18)][SerializeOrder(3)] private readonly ulong _v4;

    /// <summary>
    /// Initializes a new instance of the <see cref="Bytes32"/> struct from a 32-byte span.
    /// </summary>
    /// <param name="source">The source span containing at least 32 bytes.</param>
    /// <exception cref="ArgumentException">Thrown when the source span is smaller than 32 bytes.</exception>
    public Bytes32(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException("Source must be at least 32 bytes.", nameof(source));
        }

        ref byte srcRef = ref MemoryMarshal.GetReference(source);
        _v1 = Unsafe.ReadUnaligned<ulong>(ref srcRef);
        _v2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, 0x08));
        _v3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, 0x10));
        _v4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, 0x18));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Bytes32"/> struct from a 32-byte array.
    /// </summary>
    /// <param name="source">The source array containing exactly 32 bytes.</param>
    public Bytes32(byte[] source) : this(source.AsSpan()) { }

    /// <summary>
    /// Copies exactly 32 bytes from the source span into this buffer.
    /// </summary>
    /// <param name="source">The source span (at least 32 bytes).</param>
    /// <returns>A new <see cref="Bytes32"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when the source span is smaller than 32 bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Bytes32 CopyFrom(ReadOnlySpan<byte> source) => new(source);

    /// <summary>
    /// Writes the content of this 256-bit buffer into the destination span.
    /// </summary>
    /// <param name="destination">The destination span (at least 32 bytes).</param>
    /// <exception cref="ArgumentException">Thrown when the destination span is smaller than 32 bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Destination must be at least 32 bytes.", nameof(destination));
        }

        ref byte dstRef = ref MemoryMarshal.GetReference(destination);
        Unsafe.WriteUnaligned(ref dstRef, _v1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, 0x08), _v2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, 0x10), _v3);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, 0x18), _v4);
    }

    /// <summary>
    /// Attempts to write the content of this 256-bit buffer into the destination span.
    /// </summary>
    /// <param name="destination">The destination span.</param>
    /// <returns><see langword="true"/> if the buffer was written successfully; otherwise, <see langword="false"/> (destination too short).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryWriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            return false;
        }

        ref byte dstRef = ref MemoryMarshal.GetReference(destination);
        Unsafe.WriteUnaligned(ref dstRef, _v1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, 0x08), _v2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, 0x10), _v3);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, 0x18), _v4);
        return true;
    }

    /// <summary>
    /// Converts this 256-bit buffer to a 32-byte array.
    /// </summary>
    /// <returns>A new 32-byte array containing the buffer state.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte[] ToByteArray()
    {
        byte[] arr = new byte[Size];
        this.WriteTo(arr);
        return arr;
    }

    /// <summary>
    /// Returns a hexadecimal string representation of the 256-bit buffer.
    /// </summary>
    public override readonly string ToString()
    {
        ReadOnlySpan<byte> span = this.AsSpan();
        if (span.IsEmpty)
        {
            return string.Empty;
        }

#if NET10_0_OR_GREATER
        return Convert.ToHexString(span);
#else
        return TO_HEX_STRING_STANDARD_21(span);
#endif
    }

    /// <summary>
    /// Gets a 256-bit buffer with all bits set to zero.
    /// </summary>
    public static Bytes32 Zero => default;

    /// <summary>
    /// Parses a hexadecimal string representation into a 256-bit buffer.
    /// </summary>
    public static Bytes32 Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        if (hex.Length != 64)
        {
            throw new FormatException("The hex string must be exactly 64 characters long.");
        }

#if NET10_0_OR_GREATER
        try
        {
            return new Bytes32(Convert.FromHexString(hex));
        }
        catch (FormatException ex)
        {
            throw new FormatException("Invalid hex character or format.", ex);
        }
#else
        for (int i = 0; i < 64; i++)
        {
            char c = hex[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                throw new FormatException("Invalid hex character.");
            }
        }

        Span<byte> bytes = stackalloc byte[32];
        for (int i = 0; i < 32; i++)
        {
            bytes[i] = (byte)((GET_HEX_VAL_STANDARD_21(hex[i << 1]) << 4) + GET_HEX_VAL_STANDARD_21(hex[(i << 1) + 1]));
        }
        return new Bytes32(bytes);
#endif
    }

    /// <summary>
    /// Returns <see langword="true"/> if all bits in the buffer are zero.
    /// This comparison is performed in constant-time.
    /// </summary>
    [SerializeIgnore]
    public readonly bool IsZero
    {
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        get
        {
#if NET10_0_OR_GREATER
            ref byte a = ref Unsafe.As<ulong, byte>(ref Unsafe.AsRef(in _v1));

            if (Avx2.IsSupported)
            {
                Vector256<byte> v = Unsafe.ReadUnaligned<Vector256<byte>>(ref a);
                // Vector256.EqualsAll is available in .NET 7+ and is safer than Avx.TestZ for bytes
                return Vector256.EqualsAll(v, Vector256<byte>.Zero);
            }

            if (Sse41.IsSupported)
            {
                Vector128<byte> v1 = Unsafe.ReadUnaligned<Vector128<byte>>(ref a);
                Vector128<byte> v2 = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref a, 16));
                return Sse41.TestZ(Sse2.Or(v1, v2), Sse2.Or(v1, v2));
            }
#endif

            return (_v1 | _v2 | _v3 | _v4) == 0;
        }
    }

    /// <summary>
    /// Compares two <see cref="Bytes32"/> instances in constant-time.
    /// </summary>
    /// <param name="other">The other instance to compare.</param>
    /// <returns><see langword="true"/> if both buffers are bitwise identical.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public readonly bool Equals(Bytes32 other)
    {
#if NET10_0_OR_GREATER
        ref byte a = ref Unsafe.As<ulong, byte>(ref Unsafe.AsRef(in _v1));
        ref byte b = ref Unsafe.As<ulong, byte>(ref Unsafe.AsRef(in other._v1));

        if (Avx2.IsSupported)
        {
            Vector256<byte> v = Unsafe.ReadUnaligned<Vector256<byte>>(ref a);
            Vector256<byte> o = Unsafe.ReadUnaligned<Vector256<byte>>(ref b);
            Vector256<byte> x = Avx2.Xor(v, o);
            return Avx.TestZ(x, x);
        }

        if (Sse41.IsSupported)
        {
            Vector128<byte> v1 = Unsafe.ReadUnaligned<Vector128<byte>>(ref a);
            Vector128<byte> v2 = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref a, 16));
            Vector128<byte> o1 = Unsafe.ReadUnaligned<Vector128<byte>>(ref b);
            Vector128<byte> o2 = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref b, 16));

            Vector128<byte> x1 = Sse2.Xor(v1, o1);
            Vector128<byte> x2 = Sse2.Xor(v2, o2);
            Vector128<byte> res = Sse2.Or(x1, x2);
            return Sse41.TestZ(res, res);
        }
#endif

        // constant-time scalar fallback
        ulong res_scalar = (_v1 ^ other._v1)
                         | (_v2 ^ other._v2)
                         | (_v3 ^ other._v3)
                         | (_v4 ^ other._v4);

        return res_scalar == 0;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override readonly bool Equals(object? obj) => obj is Bytes32 other && this.Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return ((int)_v1) ^ ((int)(_v1 >> Size))
             ^ ((int)_v2) ^ ((int)(_v2 >> Size))
             ^ ((int)_v3) ^ ((int)(_v3 >> Size))
             ^ ((int)_v4) ^ ((int)(_v4 >> Size));
    }

    /// <summary>
    /// Returns a read-only span covering the 32-byte buffer.
    /// </summary>
    public readonly ReadOnlySpan<byte> AsSpan()
    {
        return MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _v1), 0x04));
    }

    /// <summary>
    /// Overwrites <paramref name="value"/> with zeroes in place.
    /// </summary>
    /// <param name="value">The value to destroy.</param>
    /// <remarks>
    /// <para>
    /// A <see cref="Bytes32"/> holding key material is a value type, so every copy of it —
    /// a local, a field, an argument — is a separate 32-byte copy of the secret that outlives the
    /// scope it was used in. Call this on each copy once it is no longer needed, so a later heap
    /// dump or stack scan cannot recover it.
    /// </para>
    /// <para>
    /// The write is not elided: the span alias defeats the JIT's dead-store analysis, the same way
    /// <c>MemorySecurity.ZeroMemory</c> does for byte buffers.
    /// </para>
    /// </remarks>
    public static void Wipe(ref Bytes32 value)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 0x01)).Clear();

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Bytes32 left, Bytes32 right) => left.Equals(right);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Bytes32 left, Bytes32 right) => !left.Equals(right);

#if !NET10_0_OR_GREATER
    private static string TO_HEX_STRING_STANDARD_21(ReadOnlySpan<byte> bytes)
    {
        Span<char> result = stackalloc char[bytes.Length * 2];
        FILL_HEX_STANDARD_21(bytes, result);
        return new string(result);
    }

    private static void FILL_HEX_STANDARD_21(ReadOnlySpan<byte> bytes, Span<char> dest)
    {
        const string hex = "0123456789ABCDEF";

        int di = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            dest[di++] = hex[b >> 4];
            dest[di++] = hex[b & 0xF];
        }
    }

    private static int GET_HEX_VAL_STANDARD_21(char hex)
    {
        int val = hex;
        return val - (val < 58 ? 48 : (val < 91 ? 55 : 87));
    }
#endif
}
