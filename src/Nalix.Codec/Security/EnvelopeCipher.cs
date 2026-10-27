// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Internal;
using Nalix.Codec.Security.Engine;
using Nalix.Codec.Security.Internal;
using Nalix.Codec.Security.Symmetric;
using Nalix.Environment.Random;

namespace Nalix.Codec.Security;

/// <summary>
/// Provides a unified high-level cryptographic façade that supports both
/// AEAD suites (e.g., CHACHA20-Poly1305) and pure symmetric stream/CTR ciphers
/// (e.g., CHACHA20, SALSA20, SPECK-CTR, XTEA-CTR).
/// <para>
/// The engine automatically dispatches to <see cref="AeadEngine"/> or
/// <see cref="SymmetricEngine"/> based on <see cref="CipherSuiteType"/>.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Envelope formats</b>
/// <list type="bullet">
/// <item>
/// <description><b>AEAD</b>: <c>header || nonce || ciphertext || tag</c> (detached tag).
/// Header size and tag size are defined by <see cref="EnvelopeFormat"/>.
/// AAD convention: <c>header || nonce || userAAD</c>.
/// </description>
/// </item>
/// <item>
/// <description><b>Symmetric/CTR</b>: <c>header || nonce || ciphertext</c> (no tag).
/// The <c>seq</c> value in the header is reused as the initial counter.
/// </description>
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>Security notes</b>
/// <list type="bullet">
/// <item><description>
/// Callers must ensure per-key nonce uniqueness for the selected suite. AEAD encryption
/// auto-generates a fresh random nonce per call; do not reuse nonces with the same key.
/// </description></item>
/// <item><description>
/// For XTEA, a 32-byte key is deterministically reduced to 16 bytes by the lower-level engine.
/// </description></item>
/// <item><description>
/// <b>Decryption failures are exceptional:</b> invalid envelopes, unsupported algorithms,
/// and authentication failures throw exceptions.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Thread safety:</b> This class is stateless and safe for concurrent use.
/// </para>
/// </remarks>
[DebuggerNonUserCode]
public static class EnvelopeCipher
{
    #region Constants

    /// <summary>
    /// Estimated number of additional bytes produced by the envelope encryption format.
    /// </summary>
    /// <value>
    /// This value includes the authentication tag length and any envelope header bytes (for example, nonce or metadata).
    /// Use this constant when sizing destination buffers for ciphertext to avoid buffer truncation.
    /// </value>
    /// <remarks>
    /// Computed as <c>EnvelopeFormat.TagSize + EnvelopeFormat.HeaderSize</c>.
    /// This is an estimate and may be conservative depending on the concrete cipher suite implementation.
    /// </remarks>
    public const int HeaderSize = EnvelopeFormat.HeaderSize;

    #endregion Constants

    #region Public API

    /// <summary>
    /// Gets the nonce length in bytes required by the specified cipher suite.
    /// </summary>
    /// <param name="type">
    /// The <see cref="CipherSuiteType"/> identifying the symmetric encryption algorithm.
    /// </param>
    /// <returns>
    /// The size of the nonce in bytes required by the cipher suite.
    /// </returns>
    /// <remarks>
    /// The nonce (number used once) is a unique value required for stream ciphers
    /// and AEAD constructions to ensure cryptographic security.
    /// <para/>
    /// For example:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <see cref="CipherSuiteType.Chacha20"/> and
    /// <see cref="CipherSuiteType.Chacha20Poly1305"/> use a nonce size defined by <see cref="ChaCha20.NonceSize"/>.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="CipherSuiteType.Salsa20"/> and
    /// <see cref="CipherSuiteType.Salsa20Poly1305"/> use a nonce size defined by <see cref="Salsa20.NonceSize"/>.
    /// </description>
    /// </item>
    /// </list>
    /// The returned value can be used when allocating buffers or generating nonces
    /// for encryption operations.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when the specified cipher suite is not supported.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetNonceLength(CipherSuiteType type) => type switch
    {
        CipherSuiteType.Chacha20 => ChaCha20.NonceSize,
        CipherSuiteType.Chacha20Poly1305 => ChaCha20.NonceSize,
        CipherSuiteType.Salsa20 => Salsa20.NonceSize,
        CipherSuiteType.Salsa20Poly1305 => Salsa20.NonceSize,
        CipherSuiteType.None or _ => throw new CipherException($"Unsupported cipher suite '{type}' for nonce length.")
    };

    /// <summary>
    /// Gets the authentication tag length in bytes produced by the specified cipher suite.
    /// </summary>
    /// <param name="type">
    /// The <see cref="CipherSuiteType"/> identifying the symmetric encryption algorithm.
    /// </param>
    /// <returns>
    /// The size of the authentication tag in bytes.
    /// Returns <c>0</c> for cipher suites that do not provide built-in authentication.
    /// </returns>
    /// <remarks>
    /// Authentication tags are produced by AEAD (Authenticated Encryption with Associated Data)
    /// algorithms to guarantee ciphertext integrity and authenticity.
    /// <para/>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <see cref="CipherSuiteType.Chacha20Poly1305"/> and
    /// <see cref="CipherSuiteType.Salsa20Poly1305"/> produce an authentication tag
    /// with size <see cref="EnvelopeFormat.TagSize"/>.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="CipherSuiteType.Chacha20"/> and
    /// <see cref="CipherSuiteType.Salsa20"/> are stream ciphers without authentication,
    /// therefore the tag length is <c>0</c>.
    /// </description>
    /// </item>
    /// </list>
    /// This value is typically used when calculating the final ciphertext buffer size
    /// or parsing envelope encryption formats.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when the specified cipher suite is not supported.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetTagLength(CipherSuiteType type) => type switch
    {
        CipherSuiteType.Chacha20 => 0,
        CipherSuiteType.Chacha20Poly1305 => EnvelopeFormat.TagSize,
        CipherSuiteType.Salsa20 => 0,
        CipherSuiteType.Salsa20Poly1305 => EnvelopeFormat.TagSize,
        CipherSuiteType.None or _ => throw new CipherException($"Unsupported cipher suite '{type}' for tag length.")
    };

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> using the selected <paramref name="algorithm"/>
    /// and writes the envelope into <paramref name="ciphertext"/>.
    /// </summary>
    /// <param name="key">Secret key (length depends on the suite).</param>
    /// <param name="plaintext">Plaintext to encrypt.</param>
    /// <param name="ciphertext">The output ciphertext.</param>
    /// <param name="aad">
    /// Optional Associated Data (AEAD suites only). It is combined with header and nonce as
    /// <c>header || nonce || userAAD</c> under the tag.
    /// Ignored for non-AEAD suites.
    /// </param>
    /// <param name="seq">
    /// Optional 32-bit sequence written into the envelope header. When omitted, a random value
    /// is used. For non-AEAD suites, this value is also used as the initial counter.
    /// </param>
    /// <param name="algorithm">
    /// Cipher suite to use. AEAD suites produce <c>header||nonce||ciphertext||tag</c>;
    /// non-AEAD (stream/CTR) suites produce <c>header||nonce||ciphertext</c>.
    /// </param>
    /// <param name="written">Written output</param>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="algorithm"/> is not recognized, the output buffer is too small,
    /// or key/nonce length errors are detected by the underlying engines.
    /// </exception>
    /// <exception cref="CipherException">
    /// Thrown when a supported authenticated cipher rejects the supplied inputs during encryption.
    /// </exception>
    /// <example>
    /// <code>
    /// // AEAD example (CHACHA20-Poly1305)
    /// var ct = EnvelopeCipher.Encrypt(key32, data, CipherSuiteType.CHACHA20_POLY1305, aad);
    ///
    /// // Stream/CTR example (CHACHA20)
    /// var ct2 = EnvelopeCipher.Encrypt(key32, data, CipherSuiteType.CHACHA20);
    /// </code>
    /// </example>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        ReadOnlySpan<byte> aad, uint? seq, CipherSuiteType algorithm, out int written)
    {
        written = 0;

        int nonceLength = GetNonceLength(algorithm);
        Span<byte> nonceStack = stackalloc byte[Math.Max(16, nonceLength)];
        Span<byte> nonce = nonceStack[..nonceLength];
        Csprng.Fill(nonce);

        switch (algorithm)
        {
            case CipherSuiteType.Salsa20:
            case CipherSuiteType.Chacha20:
                SymmetricEngine.Encrypt(key, plaintext, ciphertext, nonce, seq, algorithm, out written);
                return;

            case CipherSuiteType.None:
            case CipherSuiteType.Salsa20Poly1305:
            case CipherSuiteType.Chacha20Poly1305:
                AeadEngine.Encrypt(key, plaintext, ciphertext, nonce, aad, seq, algorithm, out written);
                return;
            default:
                throw new CipherException($"Unsupported cipher type '{algorithm}'.");
        }
    }

    /// <summary>
    /// Decrypts an encrypted envelope.
    /// </summary>
    /// <param name="key">Secret key (length depends on the suite).</param>
    /// <param name="envelope">Concatenation of <c>header || nonce || ciphertext</c> [|| <c>tag</c>].</param>
    /// <param name="plaintext">
    /// Destination buffer for the plaintext.
    /// </param>
    /// <param name="aad">
    /// Optional Associated Data (AEAD suites only). Must match the value (if any) used at encryption time.
    /// Ignored for non-AEAD suites.
    /// </param>
    /// <param name="expectedAlgorithm">The cipher suite expected for this envelope. Used to prevent algorithm downgrade attacks.</param>
    /// <param name="written">Number of plaintext bytes written on success.</param>
    /// <param name="seq">The sequence number received in the envelope.</param>
    /// <remarks>
    /// For AEAD suites, the same AAD convention is used as in encryption:
    /// <c>header || nonce || userAAD</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="envelope"/> is malformed, the destination buffer is too small,
    /// or key length validation fails in the underlying engine.
    /// </exception>
    /// <exception cref="CipherException">
    /// Thrown when authentication fails for AEAD envelopes.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the envelope declares an unsupported cipher suite.
    /// </exception>
    /// <example>
    /// <code>
    /// EnvelopeCipher.Decrypt(key32, envelope, plaintext, aad, out var written);
    /// // use plaintext[..written]
    /// </code>
    /// </example>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> envelope,
        Span<byte> plaintext,
        ReadOnlySpan<byte> aad,
        CipherSuiteType expectedAlgorithm,
        out int written, out uint seq)
    {
        if (!TryDecrypt(key, envelope, plaintext, aad, expectedAlgorithm, out written, out seq, out CipherError error))
        {
            ThrowCipherError(error, expectedAlgorithm);
        }
    }

    /// <summary>
    /// Attempts to decrypt an encrypted envelope without throwing exceptions on authentication or formatting failures.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryDecrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> envelope,
        Span<byte> plaintext,
        ReadOnlySpan<byte> aad,
        CipherSuiteType expectedAlgorithm,
        out int written, out uint seq) => TryDecrypt(key, envelope, plaintext, aad, expectedAlgorithm, out written, out seq, out _);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool TryDecrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> envelope,
        Span<byte> plaintext,
        ReadOnlySpan<byte> aad,
        CipherSuiteType expectedAlgorithm,
        out int written, out uint seq, out CipherError error)
    {
        written = 0;
        seq = 0;

        if (!EnvelopeFormat.TryParseEnvelope(envelope, out EnvelopeFormat.Envelope env, out error))
        {
            return false;
        }

        seq = env.Seq;

        // SEC-38: Enforce that the algorithm declared in the envelope matches the one 
        // expected by the session state. Prevents AEAD -> non-AEAD downgrade attacks.
        if (env.AeadType != expectedAlgorithm)
        {
            error = CipherError.AlgorithmMismatch;
            return false;
        }

        // The nonce length is attacker-controlled; the primitives throw on a wrong size, so reject it here.
        if (env.AeadType != CipherSuiteType.None && env.NonceLen != GetNonceLength(env.AeadType))
        {
            error = CipherError.InvalidNonceLength;
            return false;
        }

        switch (env.AeadType)
        {
            case CipherSuiteType.Salsa20:
            case CipherSuiteType.Chacha20:
                return SymmetricEngine.TryDecrypt(key, envelope, plaintext, out written, out error);

            case CipherSuiteType.None:
            case CipherSuiteType.Salsa20Poly1305:
            case CipherSuiteType.Chacha20Poly1305:
                return AeadEngine.TryDecrypt(key, envelope, plaintext, aad, out written, out error);
            default:
                error = CipherError.UnsupportedAlgorithm;
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCipherError(CipherError error, CipherSuiteType expectedAlgorithm)
    {
        switch (error)
        {
            case CipherError.AlgorithmMismatch:
                throw new CipherException($"Ciphertext algorithm mismatch: expected='{expectedAlgorithm}'. Potential downgrade attack or protocol state divergence.");
            case CipherError.DestinationTooSmall:
                throw new ArgumentException("The destination plaintext buffer is too small for the decrypted payload.");
            case CipherError.AuthenticationFailed:
                Throw.AeadAuthenticationFailed();
                break;
            case CipherError.UnsupportedAlgorithm:
                Throw.UnsupportedAlgorithm();
                break;
            case CipherError.Success:
            case CipherError.InvalidHeader:
            case CipherError.InvalidTagLength:
            case CipherError.EnvelopeTooShort:
            case CipherError.InvalidNonceLength:
            case CipherError.CiphertextTooShort:
            default:
                EnvelopeFormat.ThrowCipherError(error);
                break;
        }
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> using the selected <paramref name="algorithm"/>
    /// and writes the envelope into <paramref name="ciphertext"/>.
    /// </summary>
    /// <param name="key">Secret key (length depends on the suite).</param>
    /// <param name="plaintext">Plaintext to encrypt.</param>
    /// <param name="ciphertext">The output ciphertext.</param>
    /// <param name="seq">
    /// Optional 32-bit sequence written into the envelope header. When omitted, a random value
    /// is used. For non-AEAD suites, this value is also used as the initial counter.
    /// </param>
    /// <param name="algorithm">
    /// Cipher suite to use. AEAD suites produce <c>header||nonce||ciphertext||tag</c>;
    /// non-AEAD (stream/CTR) suites produce <c>header||nonce||ciphertext</c>.
    /// </param>
    /// <param name="written">Written output</param>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="algorithm"/> is not recognized, the output buffer is too small,
    /// or key/nonce length errors are detected by the underlying engines.
    /// </exception>
    /// <exception cref="CipherException">
    /// Thrown when a supported authenticated cipher rejects the supplied inputs during encryption.
    /// </exception>
    /// <example>
    /// <code>
    /// // AEAD example (CHACHA20-Poly1305)
    /// var ct = EnvelopeCipher.Encrypt(key32, data, CipherSuiteType.CHACHA20_POLY1305, aad);
    ///
    /// // Stream/CTR example (CHACHA20)
    /// var ct2 = EnvelopeCipher.Encrypt(key32, data, CipherSuiteType.CHACHA20);
    /// </code>
    /// </example>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        uint? seq, CipherSuiteType algorithm,
        out int written) => Encrypt(key, plaintext, ciphertext, default, seq, algorithm, out written);

    /// <summary>
    /// Decrypts an encrypted envelope.
    /// </summary>
    /// <param name="key">Secret key (length depends on the suite).</param>
    /// <param name="envelope">Concatenation of <c>header || nonce || ciphertext</c> [|| <c>tag</c>].</param>
    /// <param name="plaintext">
    /// Destination buffer for the plaintext.
    /// </param>
    /// <param name="receivedSeq">The sequence number received in the envelope.</param>
    /// <param name="expectedAlgorithm">The cipher suite expected for this envelope. Used to prevent algorithm downgrade attacks.</param>
    /// <param name="written">Number of plaintext bytes written on success.</param>
    /// <remarks>
    /// For AEAD suites, the same AAD convention is used as in encryption:
    /// <c>header || nonce || userAAD</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="envelope"/> is malformed, the destination buffer is too small,
    /// or key length validation fails in the underlying engine.
    /// </exception>
    /// <exception cref="CipherException">
    /// Thrown when authentication fails for AEAD envelopes.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the envelope declares an unsupported cipher suite.
    /// </exception>
    /// <example>
    /// <code>
    /// EnvelopeCipher.Decrypt(key32, envelope, plaintext, out var written);
    /// // use plaintext[..written]
    /// </code>
    /// </example>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> envelope,
        Span<byte> plaintext,
        CipherSuiteType expectedAlgorithm,
        out int written,
        out uint receivedSeq) => Decrypt(key, envelope, plaintext, default, expectedAlgorithm, out written, out receivedSeq);

    /// <summary>
    /// Attempts to decrypt an encrypted envelope without throwing exceptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryDecrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> envelope,
        Span<byte> plaintext,
        CipherSuiteType expectedAlgorithm,
        out int written,
        out uint receivedSeq) => TryDecrypt(key, envelope, plaintext, default, expectedAlgorithm, out written, out receivedSeq, out _);

    #endregion Public API
}
