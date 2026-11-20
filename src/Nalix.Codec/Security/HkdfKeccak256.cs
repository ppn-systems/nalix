// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Primitives;

namespace Nalix.Codec.Security;

/// <summary>
/// HKDF (RFC 5869) over HMAC-Keccak256 with a 32-byte output: the key derivation used by the handshake,
/// the session rekey and the datagram key schedule.
/// </summary>
public static class HkdfKeccak256
{
    /// <summary>
    /// The largest <c>info</c> accepted by <see cref="Expand"/>, in bytes.
    /// </summary>
    public const int MaxInfoLength = 256;

    /// <summary>
    /// HKDF-Extract: <c>PRK = HMAC(salt, IKM)</c>.
    /// </summary>
    /// <param name="salt">The salt; may be empty.</param>
    /// <param name="ikm">The input keying material.</param>
    /// <returns>The pseudorandom key.</returns>
    public static Bytes32 Extract(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> ikm)
    {
        Span<byte> prk = stackalloc byte[32];
        try
        {
            HmacKeccak256.Compute(salt, ikm, prk);
            return new Bytes32(prk);
        }
        finally
        {
            MemorySecurity.ZeroMemory(prk);
        }
    }

    /// <summary>
    /// HKDF-Expand for one 32-byte block: <c>OKM = HMAC(PRK, info || 0x01)</c>.
    /// </summary>
    /// <param name="prk">The pseudorandom key. The method wipes its own copy.</param>
    /// <param name="info">Context and label, at most <see cref="MaxInfoLength"/> bytes.</param>
    /// <returns>The 32-byte output keying material.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="info"/> is longer than <see cref="MaxInfoLength"/>.</exception>
    public static Bytes32 Expand(Bytes32 prk, ReadOnlySpan<byte> info)
    {
        // prk arrived by value, so this frame owns a 32-byte copy of the secret.
        try
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(info.Length, MaxInfoLength, nameof(info));

            Span<byte> okm = stackalloc byte[32];
            Span<byte> t = stackalloc byte[MaxInfoLength + 1];
            t = t[..(info.Length + 1)];

            try
            {
                info.CopyTo(t);
                t[^1] = 1; // counter

                HmacKeccak256.Compute(prk.AsSpan(), t, okm);
                return new Bytes32(okm);
            }
            finally
            {
                MemorySecurity.ZeroMemory(okm);
                MemorySecurity.ZeroMemory(t);
            }
        }
        finally
        {
            Bytes32.Wipe(ref prk);
        }
    }
}
