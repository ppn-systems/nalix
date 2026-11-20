// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Aead;

namespace Nalix.Codec.Security.Datagram;

/// <summary>
/// <see cref="IDatagramProtection"/> with ChaCha20-Poly1305 (RFC 8439) and the NDP key schedule
/// (<see cref="DatagramKeySchedule"/>). Each direction has its own secret chain and key phase.
/// </summary>
/// <remarks>Not thread-safe: the datagram session serializes calls.</remarks>
public sealed class ChaCha20Poly1305DatagramProtection : IDatagramProtection
{
    private readonly KeySet _send = new();
    private readonly KeySet _receive = new();
    private readonly KeySet _receiveNext = new();
    private readonly KeySet _receivePrevious = new();

    private bool _sendPhase;
    private bool _receivePhase;
    private bool _hasPrevious;
    private ulong _receivePhaseFirstPacket;
    private bool _disposed;

    /// <summary>
    /// Initializes the protection from the two direction secrets. Both secrets are copied; the caller keeps
    /// ownership of its values.
    /// </summary>
    /// <param name="sendSecret">The secret of the direction this side sends in.</param>
    /// <param name="receiveSecret">The secret of the direction this side receives in.</param>
    public ChaCha20Poly1305DatagramProtection(Bytes32 sendSecret, Bytes32 receiveSecret)
    {
        _send.Load(sendSecret);
        _receive.Load(receiveSecret);
        _receiveNext.Load(DatagramKeySchedule.DeriveNextSecret(receiveSecret));
        Bytes32.Wipe(ref sendSecret);
        Bytes32.Wipe(ref receiveSecret);
    }

    /// <summary>
    /// Creates the protection of one side of a datagram session (§5 of the NDP draft).
    /// </summary>
    /// <param name="sessionSecret">The stream session secret snapshotted when the ticket was issued.</param>
    /// <param name="connectionId">The datagram connection identifier.</param>
    /// <param name="ticketNonce">The 16-byte ticket nonce.</param>
    /// <param name="isServer"><see langword="true"/> for the server side.</param>
    /// <returns>The protection.</returns>
    public static ChaCha20Poly1305DatagramProtection Create(Bytes32 sessionSecret, ulong connectionId, ReadOnlySpan<byte> ticketNonce, bool isServer)
    {
        DatagramKeySchedule.DeriveDirectionSecrets(sessionSecret, connectionId, ticketNonce, out Bytes32 c2s, out Bytes32 s2c);
        try
        {
            return isServer ? new(s2c, c2s) : new(c2s, s2c);
        }
        finally
        {
            Bytes32.Wipe(ref c2s);
            Bytes32.Wipe(ref s2c);
        }
    }

    /// <inheritdoc/>
    public int TagSize => ChaCha20Poly1305.TagSize;

    /// <inheritdoc/>
    public bool SendKeyPhase => _sendPhase;

    /// <inheritdoc/>
    public void Seal(ulong packetNumber, ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, plaintext.Length + ChaCha20Poly1305.TagSize, nameof(destination));

        Span<byte> nonce = stackalloc byte[DatagramKeySchedule.IvSize];
        DatagramKeySchedule.BuildNonce(_send.Iv, packetNumber, nonce);

        _ = ChaCha20Poly1305.Encrypt(
            _send.Key, nonce, plaintext, header,
            destination[..plaintext.Length],
            destination.Slice(plaintext.Length, ChaCha20Poly1305.TagSize));
    }

    /// <inheritdoc/>
    public bool TryOpen(ulong packetNumber, bool keyPhase, ReadOnlySpan<byte> header, ReadOnlySpan<byte> ciphertextAndTag, Span<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ciphertextAndTag.Length < ChaCha20Poly1305.TagSize)
        {
            return false;
        }

        if (keyPhase == _receivePhase)
        {
            return Open(_receive, packetNumber, header, ciphertextAndTag, plaintext);
        }

        // The other phase: a late packet of the previous key, or the first packets of the next key.
        if (_hasPrevious && packetNumber < _receivePhaseFirstPacket)
        {
            return Open(_receivePrevious, packetNumber, header, ciphertextAndTag, plaintext);
        }

        if (!Open(_receiveNext, packetNumber, header, ciphertextAndTag, plaintext))
        {
            return false;
        }

        // The peer updated its key: next becomes current, current becomes previous.
        _receivePrevious.CopyFrom(_receive);
        _receive.CopyFrom(_receiveNext);
        _receiveNext.Load(DatagramKeySchedule.DeriveNextSecret(_receive.Secret));
        _hasPrevious = true;
        _receivePhase = keyPhase;
        _receivePhaseFirstPacket = packetNumber;
        return true;
    }

    /// <inheritdoc/>
    public void UpdateSendKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _send.Load(DatagramKeySchedule.DeriveNextSecret(_send.Secret));
        _sendPhase = !_sendPhase;
    }

    /// <inheritdoc/>
    public void DiscardPreviousReceiveKey()
    {
        if (_hasPrevious)
        {
            _receivePrevious.Wipe();
            _hasPrevious = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _send.Wipe();
        _receive.Wipe();
        _receiveNext.Wipe();
        _receivePrevious.Wipe();
        _hasPrevious = false;
    }

    private static bool Open(KeySet keys, ulong packetNumber, ReadOnlySpan<byte> header, ReadOnlySpan<byte> ciphertextAndTag, Span<byte> plaintext)
    {
        int length = ciphertextAndTag.Length - ChaCha20Poly1305.TagSize;
        if (plaintext.Length < length)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[DatagramKeySchedule.IvSize];
        DatagramKeySchedule.BuildNonce(keys.Iv, packetNumber, nonce);

        return ChaCha20Poly1305.Decrypt(
            keys.Key, nonce,
            ciphertextAndTag[..length], header,
            ciphertextAndTag.Slice(length, ChaCha20Poly1305.TagSize),
            plaintext[..length]) >= 0;
    }

    /// <summary>
    /// One generation of a direction: the secret it was derived from, the AEAD key and the IV.
    /// </summary>
    private sealed class KeySet
    {
        private readonly byte[] _key = new byte[32];
        private readonly byte[] _iv = new byte[DatagramKeySchedule.IvSize];
        private Bytes32 _secret;

        public Bytes32 Secret => _secret;

        public ReadOnlySpan<byte> Key => _key;

        public ReadOnlySpan<byte> Iv => _iv;

        /// <summary>Loads a generation from its secret and wipes the caller's copy.</summary>
        public void Load(Bytes32 secret)
        {
            Bytes32.Wipe(ref _secret);
            _secret = secret;

            Bytes32 key = DatagramKeySchedule.DeriveKey(secret);
            key.WriteTo(_key);
            Bytes32.Wipe(ref key);
            DatagramKeySchedule.DeriveIv(secret, _iv);
            Bytes32.Wipe(ref secret);
        }

        public void CopyFrom(KeySet other)
        {
            Bytes32.Wipe(ref _secret);
            _secret = other._secret;
            other._key.CopyTo(_key, 0);
            other._iv.CopyTo(_iv, 0);
        }

        public void Wipe()
        {
            Bytes32.Wipe(ref _secret);
            DatagramKeySchedule.Wipe(_key);
            DatagramKeySchedule.Wipe(_iv);
        }
    }
}
