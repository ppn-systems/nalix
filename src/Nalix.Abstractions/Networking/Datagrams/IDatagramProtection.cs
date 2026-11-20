// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Abstractions.Networking.Datagrams;

/// <summary>
/// Seals and opens the datagrams of one datagram session with an AEAD. The keys of each direction are
/// independent; each direction updates its own key and signals it with the key phase bit.
/// </summary>
/// <remarks>
/// Implementations are not thread-safe; the datagram session serializes calls.
/// A packet number is sealed at most once per key: the caller never reuses one.
/// </remarks>
public interface IDatagramProtection : IDisposable
{
    /// <summary>
    /// Gets the size in bytes of the authentication tag appended to every sealed payload.
    /// </summary>
    int TagSize { get; }

    /// <summary>
    /// Gets the key phase of the current send key. It flips on every <see cref="UpdateSendKey"/>.
    /// </summary>
    bool SendKeyPhase { get; }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and authenticates it together with <paramref name="header"/>.
    /// </summary>
    /// <param name="packetNumber">The full packet number; it selects the nonce.</param>
    /// <param name="header">The cleartext header, authenticated as associated data.</param>
    /// <param name="plaintext">The payload to encrypt.</param>
    /// <param name="destination">
    /// Receives the ciphertext followed by the tag; at least <c>plaintext.Length + TagSize</c> bytes. It must not
    /// overlap <paramref name="plaintext"/>.
    /// </param>
    void Seal(ulong packetNumber, ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext, Span<byte> destination);

    /// <summary>
    /// Authenticates and decrypts a received payload. A payload with the other key phase is opened with the
    /// previous key when it is older than the current phase, otherwise with the next key; when the next key
    /// succeeds it becomes the current receive key.
    /// </summary>
    /// <param name="packetNumber">The full reconstructed packet number.</param>
    /// <param name="keyPhase">The key phase bit from the header.</param>
    /// <param name="header">The cleartext header as received.</param>
    /// <param name="ciphertextAndTag">The ciphertext followed by the tag.</param>
    /// <param name="plaintext">
    /// Receives the plaintext; at least <c>ciphertextAndTag.Length - TagSize</c> bytes. It must not overlap
    /// <paramref name="ciphertextAndTag"/>.
    /// </param>
    /// <returns><see langword="true"/> when the payload is authentic.</returns>
    bool TryOpen(ulong packetNumber, bool keyPhase, ReadOnlySpan<byte> header, ReadOnlySpan<byte> ciphertextAndTag, Span<byte> plaintext);

    /// <summary>
    /// Replaces the send key with the next one in the key schedule and flips <see cref="SendKeyPhase"/>.
    /// </summary>
    void UpdateSendKey();

    /// <summary>
    /// Wipes the previous receive key, after late packets of the old phase can no longer arrive.
    /// </summary>
    void DiscardPreviousReceiveKey();
}
