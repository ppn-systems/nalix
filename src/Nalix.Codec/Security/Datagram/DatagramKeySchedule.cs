// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Primitives;

namespace Nalix.Codec.Security.Datagram;

/// <summary>
/// Key schedule of the Nalix Datagram Protocol (NDP v1), built on <see cref="HkdfKeccak256"/>:
/// <code>
/// dgram_secret = HKDF-Expand(S, "nalix-dgram/v1" || ConnectionId (u64 LE) || TicketNonce)
/// c2s_secret   = HKDF-Expand(dgram_secret, "nalix-dgram/c2s")
/// s2c_secret   = HKDF-Expand(dgram_secret, "nalix-dgram/s2c")
/// key(x)       = HKDF-Expand(x, "key")
/// iv(x)        = HKDF-Expand(x, "iv")[0..12]
/// next(x)      = HKDF-Expand(x, "nalix-dgram/ku")
/// </code>
/// </summary>
public static class DatagramKeySchedule
{
    /// <summary>
    /// Size in bytes of the ticket nonce.
    /// </summary>
    public const int TicketNonceSize = 16;

    /// <summary>
    /// Size in bytes of the AEAD nonce and of the derived IV.
    /// </summary>
    public const int IvSize = 12;

    private static ReadOnlySpan<byte> SessionLabel => "nalix-dgram/v1"u8;
    private static ReadOnlySpan<byte> ClientToServerLabel => "nalix-dgram/c2s"u8;
    private static ReadOnlySpan<byte> ServerToClientLabel => "nalix-dgram/s2c"u8;
    private static ReadOnlySpan<byte> KeyLabel => "key"u8;
    private static ReadOnlySpan<byte> IvLabel => "iv"u8;
    private static ReadOnlySpan<byte> KeyUpdateLabel => "nalix-dgram/ku"u8;

    /// <summary>
    /// Derives the client-to-server and server-to-client secrets of a datagram session.
    /// </summary>
    /// <param name="sessionSecret">The stream session secret snapshotted when the ticket was issued.</param>
    /// <param name="connectionId">The datagram connection identifier.</param>
    /// <param name="ticketNonce">The 16-byte ticket nonce.</param>
    /// <param name="clientToServer">Receives the client-to-server secret.</param>
    /// <param name="serverToClient">Receives the server-to-client secret.</param>
    /// <exception cref="ArgumentException"><paramref name="ticketNonce"/> is not 16 bytes.</exception>
    public static void DeriveDirectionSecrets(
        Bytes32 sessionSecret, ulong connectionId, ReadOnlySpan<byte> ticketNonce,
        out Bytes32 clientToServer, out Bytes32 serverToClient)
    {
        if (ticketNonce.Length != TicketNonceSize)
        {
            Bytes32.Wipe(ref sessionSecret);
            throw new ArgumentException($"The ticket nonce must be {TicketNonceSize} bytes.", nameof(ticketNonce));
        }

        Span<byte> info = stackalloc byte[SessionLabel.Length + sizeof(ulong) + TicketNonceSize];
        SessionLabel.CopyTo(info);
        BinaryPrimitives.WriteUInt64LittleEndian(info[SessionLabel.Length..], connectionId);
        ticketNonce.CopyTo(info[(SessionLabel.Length + sizeof(ulong))..]);

        Bytes32 datagramSecret = HkdfKeccak256.Expand(sessionSecret, info);
        try
        {
            clientToServer = HkdfKeccak256.Expand(datagramSecret, ClientToServerLabel);
            serverToClient = HkdfKeccak256.Expand(datagramSecret, ServerToClientLabel);
        }
        finally
        {
            Bytes32.Wipe(ref datagramSecret);
            Bytes32.Wipe(ref sessionSecret);
        }
    }

    /// <summary>
    /// Derives the 32-byte ChaCha20-Poly1305 key of a direction secret.
    /// </summary>
    public static Bytes32 DeriveKey(Bytes32 secret) => HkdfKeccak256.Expand(secret, KeyLabel);

    /// <summary>
    /// Derives the 12-byte IV of a direction secret.
    /// </summary>
    /// <param name="secret">The direction secret.</param>
    /// <param name="iv">Receives the IV; at least 12 bytes.</param>
    public static void DeriveIv(Bytes32 secret, Span<byte> iv)
    {
        Bytes32 block = HkdfKeccak256.Expand(secret, IvLabel);
        try
        {
            block.AsSpan()[..IvSize].CopyTo(iv);
        }
        finally
        {
            Bytes32.Wipe(ref block);
        }
    }

    /// <summary>
    /// Derives the next secret of a direction for a key update.
    /// </summary>
    public static Bytes32 DeriveNextSecret(Bytes32 secret) => HkdfKeccak256.Expand(secret, KeyUpdateLabel);

    /// <summary>
    /// Builds the AEAD nonce of a packet: <c>iv XOR (0x00000000 || packetNumber as u64 big-endian)</c>.
    /// </summary>
    /// <param name="iv">The 12-byte IV.</param>
    /// <param name="packetNumber">The full packet number.</param>
    /// <param name="nonce">Receives the 12-byte nonce.</param>
    public static void BuildNonce(ReadOnlySpan<byte> iv, ulong packetNumber, Span<byte> nonce)
    {
        iv[..IvSize].CopyTo(nonce);
        ulong tail = BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]) ^ packetNumber;
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], tail);
    }

    internal static void Wipe(Span<byte> data) => MemorySecurity.ZeroMemory(data);
}
