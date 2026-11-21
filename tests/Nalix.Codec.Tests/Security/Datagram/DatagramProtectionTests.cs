// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Datagram;
using Xunit;

namespace Nalix.Codec.Tests.Security.Datagram;

/// <summary>
/// NDP datagram protection: key schedule, per-direction keys, AEAD sealing with the header as AAD,
/// and key update by key phase.
/// </summary>
public sealed class DatagramProtectionTests
{
    private const ulong ConnectionId = 0x0807060504030201UL;

    private static readonly Bytes32 s_sessionSecret = new(Sequence(1, 32));
    private static readonly byte[] s_ticketNonce = Sequence(0xA0, 16);
    private static readonly byte[] s_header = Sequence(0x10, 13);
    private static readonly byte[] s_payload = "hello, datagram"u8.ToArray();

    [Fact]
    public void Expand_MatchesHandshakeRekeyDerivation()
    {
        Bytes32 secret = new(Sequence(7, 32));

        Bytes32 expected = HandshakeX25519.DeriveRekeySecret(secret);
        Bytes32 actual = HkdfKeccak256.Expand(secret, "nalix-session/rekey"u8);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Expand_WhenInfoIsTooLong_Throws()
    {
        byte[] info = new byte[HkdfKeccak256.MaxInfoLength + 1];

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => HkdfKeccak256.Expand(s_sessionSecret, info));
    }

    [Fact]
    public void KeySchedule_KnownAnswer()
    {
        DatagramKeySchedule.DeriveDirectionSecrets(s_sessionSecret, ConnectionId, s_ticketNonce, out Bytes32 c2s, out Bytes32 s2c);
        Span<byte> iv = stackalloc byte[DatagramKeySchedule.IvSize];
        DatagramKeySchedule.DeriveIv(c2s, iv);

        Assert.Equal(KnownAnswers.ClientToServerSecret, Convert.ToHexString(c2s.AsSpan()));
        Assert.Equal(KnownAnswers.ServerToClientSecret, Convert.ToHexString(s2c.AsSpan()));
        Assert.Equal(KnownAnswers.ClientToServerKey, Convert.ToHexString(DatagramKeySchedule.DeriveKey(c2s).AsSpan()));
        Assert.Equal(KnownAnswers.ClientToServerIv, Convert.ToHexString(iv));
        Assert.Equal(KnownAnswers.ClientToServerNextSecret, Convert.ToHexString(DatagramKeySchedule.DeriveNextSecret(c2s).AsSpan()));
    }

    [Fact]
    public void Seal_KnownAnswer()
    {
        using ChaCha20Poly1305DatagramProtection client = ChaCha20Poly1305DatagramProtection.Create(s_sessionSecret, ConnectionId, s_ticketNonce, isServer: false);
        byte[] sealedPayload = new byte[s_payload.Length + client.TagSize];

        client.Seal(1, s_header, s_payload, sealedPayload);

        Assert.Equal(KnownAnswers.SealedPacket1, Convert.ToHexString(sealedPayload));
    }

    [Fact]
    public void BuildNonce_XorsPacketNumberIntoLastEightBytes()
    {
        byte[] iv = Sequence(0, 12);
        Span<byte> nonce = stackalloc byte[12];

        DatagramKeySchedule.BuildNonce(iv, 0x0102UL, nonce);

        Assert.Equal("000102030405060708090B09", Convert.ToHexString(nonce));
    }

    [Fact]
    public void SealOpen_RoundTripsInBothDirections()
    {
        (ChaCha20Poly1305DatagramProtection client, ChaCha20Poly1305DatagramProtection server) = CreatePair();
        using (client)
        using (server)
        {
            Assert.Equal(s_payload, RoundTrip(client, server, 5));
            Assert.Equal(s_payload, RoundTrip(server, client, 5));
        }
    }

    [Fact]
    public void Open_WhenHeaderPayloadOrPacketNumberChanges_Fails()
    {
        (ChaCha20Poly1305DatagramProtection client, ChaCha20Poly1305DatagramProtection server) = CreatePair();
        using (client)
        using (server)
        {
            byte[] sealedPayload = Seal(client, 9);
            byte[] plaintext = new byte[s_payload.Length];

            byte[] header = (byte[])s_header.Clone();
            header[3] ^= 1;
            Assert.False(server.TryOpen(9, false, header, sealedPayload, plaintext));

            byte[] tampered = (byte[])sealedPayload.Clone();
            tampered[0] ^= 1;
            Assert.False(server.TryOpen(9, false, s_header, tampered, plaintext));

            Assert.False(server.TryOpen(10, false, s_header, sealedPayload, plaintext));
            Assert.False(server.TryOpen(9, false, s_header, sealedPayload.AsSpan(0, 15), plaintext));
            Assert.True(server.TryOpen(9, false, s_header, sealedPayload, plaintext));
        }
    }

    [Fact]
    public void Open_WhenSealedWithOwnSendKey_Fails()
    {
        (ChaCha20Poly1305DatagramProtection client, ChaCha20Poly1305DatagramProtection server) = CreatePair();
        using (client)
        using (server)
        {
            byte[] sealedPayload = Seal(client, 3);

            // Directions use different keys: the client cannot open what it sent.
            Assert.False(client.TryOpen(3, false, s_header, sealedPayload, new byte[s_payload.Length]));
        }
    }

    [Fact]
    public void KeyUpdate_ReceiverFollowsPhaseAndKeepsPreviousKeyForLatePackets()
    {
        (ChaCha20Poly1305DatagramProtection client, ChaCha20Poly1305DatagramProtection server) = CreatePair();
        using (client)
        using (server)
        {
            byte[] latePhase0 = Seal(client, 10);

            client.UpdateSendKey();
            Assert.True(client.SendKeyPhase);

            byte[] phase1 = Seal(client, 11);
            byte[] plaintext = new byte[s_payload.Length];

            // The first packet of the new phase switches the receiver to the next key.
            Assert.True(server.TryOpen(11, keyPhase: true, s_header, phase1, plaintext));
            Assert.Equal(s_payload, plaintext);

            // A late packet of the old phase is still accepted with the previous key.
            Assert.True(server.TryOpen(10, keyPhase: false, s_header, latePhase0, plaintext));

            server.DiscardPreviousReceiveKey();
            Assert.False(server.TryOpen(10, keyPhase: false, s_header, latePhase0, plaintext));

            // A second update moves to phase 0 with the third generation of keys.
            client.UpdateSendKey();
            Assert.False(client.SendKeyPhase);
            Assert.Equal(s_payload, RoundTrip(client, server, 12, keyPhase: false));
        }
    }

    [Fact]
    public void Open_WhenPhaseFlipsWithForgedPayload_DoesNotSwitchKeys()
    {
        (ChaCha20Poly1305DatagramProtection client, ChaCha20Poly1305DatagramProtection server) = CreatePair();
        using (client)
        using (server)
        {
            byte[] forged = new byte[s_payload.Length + 16];
            Assert.False(server.TryOpen(20, keyPhase: true, s_header, forged, new byte[s_payload.Length]));

            // The receiver still uses phase 0.
            Assert.Equal(s_payload, RoundTrip(client, server, 21));
        }
    }

    [Fact]
    public void Factory_CreatesMatchingSides()
    {
        using var client = (ChaCha20Poly1305DatagramProtection)ChaCha20Poly1305DatagramProtectionFactory.Instance.Create(s_sessionSecret, ConnectionId, s_ticketNonce, isServer: false);
        using var server = (ChaCha20Poly1305DatagramProtection)ChaCha20Poly1305DatagramProtectionFactory.Instance.Create(s_sessionSecret, ConnectionId, s_ticketNonce, isServer: true);

        Assert.Equal(s_payload, RoundTrip(client, server, 1));
    }

    [Fact]
    public void Seal_WhenDisposed_Throws()
    {
        (ChaCha20Poly1305DatagramProtection client, ChaCha20Poly1305DatagramProtection server) = CreatePair();
        server.Dispose();
        client.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => Seal(client, 1));
    }

    private static (ChaCha20Poly1305DatagramProtection Client, ChaCha20Poly1305DatagramProtection Server) CreatePair()
        => (ChaCha20Poly1305DatagramProtection.Create(s_sessionSecret, ConnectionId, s_ticketNonce, isServer: false),
            ChaCha20Poly1305DatagramProtection.Create(s_sessionSecret, ConnectionId, s_ticketNonce, isServer: true));

    private static byte[] Seal(ChaCha20Poly1305DatagramProtection sender, ulong packetNumber)
    {
        byte[] sealedPayload = new byte[s_payload.Length + sender.TagSize];
        sender.Seal(packetNumber, s_header, s_payload, sealedPayload);
        return sealedPayload;
    }

    private static byte[] RoundTrip(ChaCha20Poly1305DatagramProtection sender, ChaCha20Poly1305DatagramProtection receiver, ulong packetNumber, bool keyPhase = false)
    {
        byte[] sealedPayload = Seal(sender, packetNumber);
        byte[] plaintext = new byte[s_payload.Length];
        Assert.True(receiver.TryOpen(packetNumber, keyPhase, s_header, sealedPayload, plaintext));
        return plaintext;
    }

    private static byte[] Sequence(byte start, int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    /// <summary>
    /// Regression vectors, also for checking other client implementations byte for byte: session secret 01..20, ConnectionId 0x0807060504030201,
    /// ticket nonce A0..AF, header 10..1C, payload "hello, datagram", packet number 1.
    /// </summary>
    private static class KnownAnswers
    {
        public const string ClientToServerSecret = "94F12FE2D2FD64F344D8E27B6C5FCC7692A0A90A3C0D9DF0A53048750D86D5C0";
        public const string ServerToClientSecret = "898D53098393A57032C787A3E2E1A8A06E7850EA157402DD9F317A136630B310";
        public const string ClientToServerKey = "746BE3BC55027D98960559DF6AED1F4B54A8BBEC217EDD48886743EA913149F2";
        public const string ClientToServerIv = "CCF182B22B05173AAC346384";
        public const string ClientToServerNextSecret = "64B4B009419B8BE182C1FCDF3141D3E469489A539B8AA18B6BB3FEC72178F6D9";
        public const string SealedPacket1 = "F690E84B48FE1758CE303132D5354EC405E358DD6E19F3FC86FC3398E1E6DD";
    }
}
