// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Internal;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Internal;
using Nalix.Codec.Security.Primitives;
using Nalix.Environment.Memory;
using Nalix.Environment.Random;

namespace Nalix.Codec.Security.Engine;

/// <summary>
/// Provides high-level APIs to encrypt and decrypt AEAD envelopes in the
/// <c>AeadFormat</c> layout: <c>header || nonce || ciphertext || tag</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Envelope structure:</strong>
/// <list type="bullet">
/// <item><description><c>header</c>: fixed <see cref="EnvelopeFormat.HeaderSize"/> bytes (contains magic, algorithm id, flags, nonce length, sequence).</description></item>
/// <item><description><c>nonce</c>: suite-specific length (e.g., 12 for CHACHA20, 8 for SALSA20, 16 for SPECK, 8 for XTEA).</description></item>
/// <item><description><c>ciphertext</c>: same length as plaintext.</description></item>
/// <item><description><c>tag</c>: authentication tag of <see cref="EnvelopeFormat.TagSize"/> bytes (detached).</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>AEAD AAD convention:</strong> The engine authenticates <c>header || nonce || userAAD</c>.
/// This binds the header and nonce into the authentication domain and prevents misuse.
/// </para>
/// <para>
/// <strong>Security notes:</strong>
/// <list type="bullet">
/// <item><description>Callers must supply a unique nonce per key for the chosen algorithm,
/// except where <see cref="Encrypt"/> auto-generates a random nonce.</description></item>
/// <item><description>This engine favors Span-first patterns and clears temporary sensitive buffers when possible.</description></item>
/// </list>
/// </para>
/// </remarks>
[System.Diagnostics.DebuggerNonUserCode]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class AeadEngine
{
    /// <summary>
    /// Encrypts plaintext and returns an AEAD envelope:
    /// <c>header || nonce || ciphertext || tag</c>.
    /// </summary>
    /// <param name="key">Secret key (length depends on suite; see remarks).</param>
    /// <param name="plaintext">Plaintext to encrypt.</param>
    /// <param name="nonce">Nonce</param>
    /// <param name="ciphertext">The output ciphertext</param>
    /// <param name="aad">Optional associated data to be authenticated (not encrypted).</param>
    /// <param name="seq">Optional 4-byte sequence number stored in the header; if null, a random value is used.</param>
    /// <param name="algorithm">
    /// Optional algorithm name. Defaults to <c>"CHACHA20"</c>.
    /// Supported aliases:
    /// <list type="bullet">
    /// <item><description><c>"chacha"</c>, <c>"chacha20"</c>, <c>"chacha20-poly1305"</c></description></item>
    /// <item><description><c>"salsa"</c>, <c>"salsa20"</c>, <c>"salsa20-poly1305"</c></description></item>
    /// </list>
    /// </param>
    /// <param name="written">Write length.</param>
    /// <exception cref="System.ArgumentException">
    /// Thrown if the output buffer is too small, the algorithm is unsupported, or the key length is invalid for the chosen suite.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Expected key lengths:
    /// <list type="bullet">
    /// <item><description><c>CHACHA20-Poly1305</c>: 32 bytes</description></item>
    /// <item><description><c>SALSA20-Poly1305</c>: 16 or 32 bytes</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public static void Encrypt(
        [System.Diagnostics.CodeAnalysis.NotNull] System.ReadOnlySpan<byte> key,
        [System.Diagnostics.CodeAnalysis.NotNull] System.ReadOnlySpan<byte> plaintext,
        [System.Diagnostics.CodeAnalysis.NotNull] System.Span<byte> ciphertext,
        [System.Diagnostics.CodeAnalysis.NotNull] System.ReadOnlySpan<byte> nonce,
        [System.Diagnostics.CodeAnalysis.NotNull] System.ReadOnlySpan<byte> aad,
        uint? seq, CipherSuiteType algorithm,
        out int written)
    {
        written = 0;
        uint seqVal = seq ?? Csprng.NextUInt32();
        int total = EnvelopeFormat.HeaderSize + nonce.Length + plaintext.Length + EnvelopeFormat.TagSize;

        if (ciphertext.Length < total)
        {
            throw new System.ArgumentException(
                $"The destination ciphertext buffer is too small for the generated envelope. Required: {total} bytes, Provided: {ciphertext.Length} bytes, Missing: {total - ciphertext.Length} bytes.",
                nameof(ciphertext));
        }

        // Instead of renting a temporary buffer, encrypt directly into the destination
        // ciphertext buffer at the correct offset to avoid Rent/Span slicing issues.
        int ctOffset = EnvelopeFormat.HeaderSize + nonce.Length;
        System.Span<byte> ctDestination = ciphertext.Slice(ctOffset, plaintext.Length);
        System.Span<byte> tagDestination = ciphertext.Slice(ctOffset + plaintext.Length, EnvelopeFormat.TagSize);
        System.Span<byte> header = stackalloc byte[EnvelopeFormat.HeaderSize];

        EnvelopeHeader headerStruct = new(EnvelopeFormat.CurrentVersion, algorithm, 0, (byte)nonce.Length, seqVal);
        EnvelopeHeader.Encode(header, in headerStruct);

        byte[]? rentedAad = null;
        int authenticatedDataLength;
        checked
        {
            authenticatedDataLength = header.Length + nonce.Length + aad.Length;
        }
        System.Span<byte> authenticatedData = authenticatedDataLength <= 256
            ? stackalloc byte[authenticatedDataLength]
            : (rentedAad = BufferLease.ByteArrayPool.Rent(authenticatedDataLength)).AsSpan(0, authenticatedDataLength);

        header.CopyTo(authenticatedData);
        nonce.CopyTo(authenticatedData[header.Length..]);
        aad.CopyTo(authenticatedData[(header.Length + nonce.Length)..]);

        try
        {
            switch (algorithm)
            {
                case CipherSuiteType.Chacha20Poly1305:
                    _ = ChaCha20Poly1305.Encrypt(key, nonce, plaintext, authenticatedData, ctDestination, tagDestination);
                    break;

                case CipherSuiteType.Salsa20Poly1305:
                    _ = Salsa20Poly1305.Encrypt(key, nonce, plaintext, authenticatedData, ctDestination, tagDestination);
                    break;
                case CipherSuiteType.None:
                case CipherSuiteType.Salsa20:
                case CipherSuiteType.Chacha20:
                default:
                    Throw.UnsupportedAlgorithm();
                    return;
            }

            _ = EnvelopeFormat.WriteEnvelope(ciphertext[..total], algorithm, 0, seqVal, nonce, ctDestination, tagDestination);
            written = total;
        }
        finally
        {
            if (rentedAad is not null)
            {
                MemorySecurity.ZeroMemory(rentedAad.AsSpan(0, authenticatedDataLength));
                BufferLease.ByteArrayPool.Return(rentedAad);
            }
        }
    }

    /// <summary>
    /// Attempts to decrypt an AEAD envelope and, if authentication succeeds,
    /// outputs the plaintext.
    /// </summary>
    /// <param name="key">Secret key (length depends on suite; see <see cref="Encrypt"/> remarks).</param>
    /// <param name="envelope">Concatenation of <c>header || nonce || ciphertext || tag</c>.</param>
    /// <param name="plaintext">
    /// On success, receives a newly allocated array containing the plaintext;
    /// otherwise set to <c>null</c>.
    /// </param>
    /// <param name="aad">Optional associated data to be authenticated (not encrypted).</param>
    /// <param name="written">Number of plaintext bytes written on success.</param>
    /// <exception cref="System.ArgumentException">Thrown when the envelope is invalid or the destination buffer is too small.</exception>
    /// <exception cref="CipherException">Thrown when authentication fails.</exception>
    /// <exception cref="System.NotSupportedException">Thrown when the envelope algorithm is not supported.</exception>
    /// <remarks>
    /// The same AAD convention is used as in <see cref="Encrypt"/>:
    /// <c>header || nonce || userAAD</c>.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static void Decrypt(
        [System.Diagnostics.CodeAnalysis.NotNull] System.ReadOnlySpan<byte> key,
        [System.Diagnostics.CodeAnalysis.NotNull] System.ReadOnlySpan<byte> envelope,
        [System.Diagnostics.CodeAnalysis.NotNull] System.Span<byte> plaintext,
        System.ReadOnlySpan<byte> aad,
        out int written)
    {
        if (!TryDecrypt(key, envelope, plaintext, aad, out written, out CipherError error))
        {
            ThrowCipherError(error);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static bool TryDecrypt(
        [System.Diagnostics.CodeAnalysis.NotNull] System.ReadOnlySpan<byte> key,
        [System.Diagnostics.CodeAnalysis.NotNull] System.ReadOnlySpan<byte> envelope,
        [System.Diagnostics.CodeAnalysis.NotNull] System.Span<byte> plaintext,
        System.ReadOnlySpan<byte> aad,
        out int written,
        out CipherError error)
    {
        written = 0;
        if (!EnvelopeFormat.TryParseEnvelope(envelope, out EnvelopeFormat.Envelope env, out error))
        {
            return false;
        }

        int ctLen = env.Ciphertext.Length;

        if (plaintext.Length < env.Ciphertext.Length)
        {
            error = CipherError.DestinationTooSmall;
            return false;
        }

        System.Span<byte> ptSlice = plaintext[..ctLen];
        byte[]? rentedAad = null;
        int authenticatedDataLength;
        checked
        {
            authenticatedDataLength = env.Header.Length + env.Nonce.Length + aad.Length;
        }
        System.Span<byte> authenticatedData = authenticatedDataLength <= 256
            ? stackalloc byte[authenticatedDataLength]
            : (rentedAad = BufferLease.ByteArrayPool.Rent(authenticatedDataLength)).AsSpan(0, authenticatedDataLength);

        env.Header.CopyTo(authenticatedData);
        env.Nonce.CopyTo(authenticatedData[env.Header.Length..]);
        aad.CopyTo(authenticatedData[(env.Header.Length + env.Nonce.Length)..]);

        try
        {
            int result;
            switch (env.AeadType)
            {
                case CipherSuiteType.Chacha20Poly1305:
                    result = ChaCha20Poly1305.Decrypt(key, env.Nonce, env.Ciphertext, authenticatedData, env.Tag, ptSlice);
                    break;

                case CipherSuiteType.Salsa20Poly1305:
                    result = Salsa20Poly1305.Decrypt(key, env.Nonce, env.Ciphertext, authenticatedData, env.Tag, ptSlice);
                    break;

                case CipherSuiteType.None:
                case CipherSuiteType.Salsa20:
                case CipherSuiteType.Chacha20:
                default:
                    error = CipherError.UnsupportedAlgorithm;
                    return false;
            }

            if (result < 0)
            {
                error = CipherError.AuthenticationFailed;
                return false;
            }

            written = result;
            error = CipherError.Success;
            return true;
        }
        finally
        {
            if (rentedAad is not null)
            {
                MemorySecurity.ZeroMemory(rentedAad.AsSpan(0, authenticatedDataLength));
                BufferLease.ByteArrayPool.Return(rentedAad);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowCipherError(CipherError error)
    {
        switch (error)
        {
            case CipherError.DestinationTooSmall:
                throw new System.ArgumentException("The destination plaintext buffer is too small for the decrypted payload.");
            case CipherError.AuthenticationFailed:
                Throw.AeadAuthenticationFailed();
                break;
            case CipherError.UnsupportedAlgorithm:
                Throw.UnsupportedAlgorithm();
                break;
            case CipherError.Success:
            case CipherError.InvalidHeader:
            case CipherError.EnvelopeTooShort:
            case CipherError.InvalidTagLength:
            case CipherError.AlgorithmMismatch:
            case CipherError.InvalidNonceLength:
            case CipherError.CiphertextTooShort:
            default:
                EnvelopeFormat.ThrowCipherError(error);
                break;
        }
    }
}
