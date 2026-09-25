// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Primitives;
using Nalix.Environment.Random;

namespace Nalix.Codec.Security.Asymmetric;

/// <summary>
/// Provides methods for generating and using X25519 key pairs for elliptic curve Diffie–Hellman (ECDH) 
/// key agreement based on Curve25519 (RFC 7748).
/// </summary>
public static class X25519
{
    /// <summary>
    /// Size in bytes of an X25519 private key, public key, and derived shared secret.
    /// </summary>
    public const int KeySize = 32;

    /// <summary>
    /// Represents an X25519 key pair consisting of a private key and a public key.
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    public struct X25519KeyPair
    {
        /// <summary>
        /// The private key (32 bytes).
        /// </summary>
        public Bytes32 PrivateKey { get; set; }

        /// <summary>
        /// The public key (32 bytes).
        /// </summary>
        public Bytes32 PublicKey { get; set; }

        /// <summary>
        /// Overwrites both keys with zeroes, leaving the pair unusable.
        /// </summary>
        /// <remarks>
        /// Call this once the agreement that needed <see cref="PrivateKey"/> has run. The pair is a
        /// value type, so an ephemeral private key otherwise stays readable in whatever frame — or,
        /// in an <c>async</c> method, whatever heap-allocated state machine — the local lived in.
        /// </remarks>
        public void Wipe()
            => System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref this, 0x01)).Clear();
    }

    /// <summary>
    /// Generates a new X25519 key pair with a cryptographically secure random private key.
    /// </summary>
    /// <returns>A new <see cref="X25519KeyPair"/> containing both private and public keys.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNull]
    public static X25519KeyPair GenerateKeyPair()
    {
        Span<byte> priv = stackalloc byte[KeySize];
        Span<byte> pubBuf = stackalloc byte[KeySize];
        try
        {
            Csprng.Fill(priv);

            // Clamp per https://cr.yp.to/ecdh.html
            priv[0] &= 248;
            priv[31] &= 127;
            priv[31] |= 64;

            Curve25519.ScalarMultiplication(priv, Curve25519.Basepoint, pubBuf);

            X25519KeyPair key = new()
            {
                PrivateKey = new Bytes32(priv),
                PublicKey = new Bytes32(pubBuf)
            };
            return key;
        }
        finally
        {
            MemorySecurity.ZeroMemory(priv);
            MemorySecurity.ZeroMemory(pubBuf);
        }
    }

    /// <summary>
    /// Derives the public key from a provided 32-byte private key.
    /// </summary>
    /// <param name="privateKey">The 32-byte private key used to derive the key pair.</param>
    /// <returns>An <see cref="X25519KeyPair"/> instance initialized with the provided private key and its derived public key.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNull]
    public static X25519KeyPair GenerateKeyFromPrivateKey(Bytes32 privateKey)
    {
        Span<byte> pubBuf = stackalloc byte[KeySize];
        try
        {
            Curve25519.ScalarMultiplication(privateKey.AsSpan(), Curve25519.Basepoint, pubBuf);

            X25519KeyPair key = new()
            {
                PrivateKey = privateKey,
                PublicKey = new Bytes32(pubBuf)
            };
            return key;
        }
        finally
        {
            MemorySecurity.ZeroMemory(pubBuf);
        }
    }

    /// <summary>
    /// Computes a shared secret via X25519 scalar multiplication 
    /// (<paramref name="myPrivateKey"/> × <paramref name="otherPublicKey"/>).
    /// </summary>
    /// <param name="myPrivateKey">The local 32-byte private key.</param>
    /// <param name="otherPublicKey">The remote 32-byte public key.</param>
    /// <returns>A 32-byte shared secret that can be used for session key derivation.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNull]
    public static Bytes32 Agreement(Bytes32 myPrivateKey, Bytes32 otherPublicKey)
    {
        Span<byte> secretBuf = stackalloc byte[KeySize];
        try
        {
            Curve25519.ScalarMultiplication(myPrivateKey.AsSpan(), otherPublicKey.AsSpan(), secretBuf);
            return new Bytes32(secretBuf);
        }
        finally
        {
            MemorySecurity.ZeroMemory(secretBuf);
        }
    }
}
