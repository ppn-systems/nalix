// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;

namespace Nalix.Codec.Security.Hashing;

/// <summary>
/// Provides high-performance HMAC implementation using Keccak-256 (original).
/// </summary>
public static class HmacKeccak256
{
    private const int BlockSize = 136;
    private const int HashSize = 32;

    /// <summary>
    /// Computes HMAC-Keccak256 of the specified data using the given key.
    /// </summary>
    /// <param name="key">The HMAC key.</param>
    /// <param name="data">The data to authenticate.</param>
    /// <param name="output">The destination buffer (must be at least 32 bytes).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> output)
    {
        if (output.Length < HashSize)
        {
            throw new ArgumentException($"Output buffer must be at least {HashSize} bytes.", nameof(output));
        }

        // Prepare key block
        Span<byte> k0 = stackalloc byte[BlockSize];
        if (key.Length > BlockSize)
        {
            Keccak256.HashData(key, k0[..HashSize]);
        }
        else
        {
            key.CopyTo(k0);
        }

        // ipad/opad
        Span<byte> ipad = stackalloc byte[BlockSize];
        Span<byte> opad = stackalloc byte[BlockSize];
        for (int i = 0; i < BlockSize; i++)
        {
            byte b = k0[i];
            ipad[i] = (byte)(b ^ 0x36);
            opad[i] = (byte)(b ^ 0x5c);
        }

        // inner = H(ipad || data)
        Span<byte> inner = stackalloc byte[HashSize];
        {
            Keccak256.Sponge hInner = new();
            hInner.Absorb(ipad);
            hInner.Absorb(data);
            hInner.PadAndSqueeze(inner);
        }

        // outer = H(opad || inner)
        Keccak256.Sponge hOuter = new();
        hOuter.Absorb(opad);
        hOuter.Absorb(inner);
        hOuter.PadAndSqueeze(output[..HashSize]);

        // Clean up sensitive data on stack
        k0.Clear();
        ipad.Clear();
        opad.Clear();
        inner.Clear();
    }
}
