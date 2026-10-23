// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Asymmetric;

namespace Nalix.Codec.Tests.Security;

/// <summary>
/// Protocol-level tests for the <see cref="HandshakeX25519"/> key-exchange transcript:
/// a full client/server exchange, MITM-modification detection (transcript binds all four
/// public/nonce values), and key-ratchet (rekey) secret evolution/isolation.
/// </summary>
public sealed class HandshakeX25519ProtocolTests
{
    private static Bytes32 RandomNonce(byte seed)
    {
        byte[] raw = new byte[32];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(seed + i);
        }

        return new Bytes32(raw);
    }

    [Fact]
    public void Handshake_WithFullClientServerExchange_ProducesMatchingKeysAndProofs()
    {
        X25519.X25519KeyPair client = X25519.GenerateKeyPair();
        X25519.X25519KeyPair server = X25519.GenerateKeyPair();
        X25519.X25519KeyPair serverStatic = X25519.GenerateKeyPair();

        Bytes32 clientNonce = RandomNonce(1);
        Bytes32 serverNonce = RandomNonce(101);

        // Client side derivation.
        Bytes32 eeClient = X25519.Agreement(client.PrivateKey, server.PublicKey);
        Bytes32 seClient = X25519.Agreement(client.PrivateKey, serverStatic.PublicKey);
        Bytes32 masterClient = HandshakeX25519.ComputeMasterSecret(eeClient, seClient);
        Bytes32 transcriptClient = HandshakeX25519.ComputeTranscriptHash(client.PublicKey, clientNonce, server.PublicKey, serverNonce);
        Bytes32 sessionKeyClient = HandshakeX25519.DeriveSessionKey(masterClient, clientNonce, serverNonce, transcriptClient);
        Bytes32 serverProofClient = HandshakeX25519.ComputeServerProof(masterClient, transcriptClient);
        Bytes32 clientProofClient = HandshakeX25519.ComputeClientProof(masterClient, transcriptClient);

        // Server side derivation (independent computation, mirrors the client's math).
        Bytes32 eeServer = X25519.Agreement(server.PrivateKey, client.PublicKey);
        Bytes32 seServer = X25519.Agreement(serverStatic.PrivateKey, client.PublicKey);
        Bytes32 masterServer = HandshakeX25519.ComputeMasterSecret(eeServer, seServer);
        Bytes32 transcriptServer = HandshakeX25519.ComputeTranscriptHash(client.PublicKey, clientNonce, server.PublicKey, serverNonce);
        Bytes32 sessionKeyServer = HandshakeX25519.DeriveSessionKey(masterServer, clientNonce, serverNonce, transcriptServer);
        Bytes32 serverProofServer = HandshakeX25519.ComputeServerProof(masterServer, transcriptServer);
        Bytes32 clientProofServer = HandshakeX25519.ComputeClientProof(masterServer, transcriptServer);

        Assert.Equal(masterClient.AsSpan().ToArray(), masterServer.AsSpan().ToArray());
        Assert.Equal(sessionKeyClient.AsSpan().ToArray(), sessionKeyServer.AsSpan().ToArray());
        Assert.Equal(serverProofClient.AsSpan().ToArray(), serverProofServer.AsSpan().ToArray());
        Assert.Equal(clientProofClient.AsSpan().ToArray(), clientProofServer.AsSpan().ToArray());
    }

    /// <summary>
    /// MITM protection mechanism: the transcript hash binds all four handshake values
    /// (client public key, client nonce, server public key, server nonce). Substituting any
    /// one of them (as an attacker splicing in their own ephemeral key would) must change the
    /// transcript hash and therefore every value derived from it — detectable via proof mismatch.
    /// </summary>
    [Theory]
    [InlineData(0)] // tamper clientPublicKey
    [InlineData(1)] // tamper clientNonce
    [InlineData(2)] // tamper serverPublicKey
    [InlineData(3)] // tamper serverNonce
    public void ComputeTranscriptHash_WhenAnyInputIsTampered_ChangesHash(int fieldToTamper)
    {
        X25519.X25519KeyPair client = X25519.GenerateKeyPair();
        X25519.X25519KeyPair server = X25519.GenerateKeyPair();
        Bytes32 clientNonce = RandomNonce(1);
        Bytes32 serverNonce = RandomNonce(101);

        Bytes32 genuineHash = HandshakeX25519.ComputeTranscriptHash(
            client.PublicKey, clientNonce, server.PublicKey, serverNonce);

        X25519.X25519KeyPair attacker = X25519.GenerateKeyPair();
        Bytes32 tamperedHash = fieldToTamper switch
        {
            0 => HandshakeX25519.ComputeTranscriptHash(attacker.PublicKey, clientNonce, server.PublicKey, serverNonce),
            1 => HandshakeX25519.ComputeTranscriptHash(client.PublicKey, RandomNonce(2), server.PublicKey, serverNonce),
            2 => HandshakeX25519.ComputeTranscriptHash(client.PublicKey, clientNonce, attacker.PublicKey, serverNonce),
            _ => HandshakeX25519.ComputeTranscriptHash(client.PublicKey, clientNonce, server.PublicKey, RandomNonce(102)),
        };

        Assert.NotEqual(genuineHash.AsSpan().ToArray(), tamperedHash.AsSpan().ToArray());
    }

    /// <summary>
    /// A MITM that substitutes its own ephemeral key for the server's must produce a session
    /// key/proof that does not match what the real server derives, so the legitimate client can
    /// detect the mismatch during proof verification.
    /// </summary>
    [Fact]
    public void Handshake_WhenServerPublicKeyIsSubstituted_ProducesDivergentProof()
    {
        X25519.X25519KeyPair client = X25519.GenerateKeyPair();
        X25519.X25519KeyPair realServer = X25519.GenerateKeyPair();
        X25519.X25519KeyPair realServerStatic = X25519.GenerateKeyPair();
        X25519.X25519KeyPair mitm = X25519.GenerateKeyPair();

        Bytes32 clientNonce = RandomNonce(3);
        Bytes32 serverNonce = RandomNonce(103);

        // Client believes it is talking to realServer's static key, but the MITM's ephemeral
        // public key was substituted in transit for realServer's ephemeral key.
        Bytes32 eeClient = X25519.Agreement(client.PrivateKey, mitm.PublicKey);
        Bytes32 seClient = X25519.Agreement(client.PrivateKey, realServerStatic.PublicKey);
        Bytes32 masterClient = HandshakeX25519.ComputeMasterSecret(eeClient, seClient);
        Bytes32 transcriptClient = HandshakeX25519.ComputeTranscriptHash(client.PublicKey, clientNonce, mitm.PublicKey, serverNonce);
        Bytes32 clientDerivedServerProof = HandshakeX25519.ComputeServerProof(masterClient, transcriptClient);

        // The real server computes its proof using its own (unsubstituted) ephemeral key.
        Bytes32 eeServer = X25519.Agreement(realServer.PrivateKey, client.PublicKey);
        Bytes32 seServer = X25519.Agreement(realServerStatic.PrivateKey, client.PublicKey);
        Bytes32 masterServer = HandshakeX25519.ComputeMasterSecret(eeServer, seServer);
        Bytes32 transcriptServer = HandshakeX25519.ComputeTranscriptHash(client.PublicKey, clientNonce, realServer.PublicKey, serverNonce);
        Bytes32 realServerProof = HandshakeX25519.ComputeServerProof(masterServer, transcriptServer);

        Assert.NotEqual(realServerProof.AsSpan().ToArray(), clientDerivedServerProof.AsSpan().ToArray());
    }

    [Fact]
    public void DeriveRekeySecret_WithSameInput_IsDeterministicAndDiffersFromSessionKey()
    {
        Bytes32 sessionKey = RandomNonce(7);

        Bytes32 rekey1 = HandshakeX25519.DeriveRekeySecret(sessionKey);
        Bytes32 rekey1Again = HandshakeX25519.DeriveRekeySecret(sessionKey);
        Bytes32 rekey2 = HandshakeX25519.DeriveRekeySecret(rekey1);

        Assert.NotEqual(sessionKey.AsSpan().ToArray(), rekey1.AsSpan().ToArray());
        Assert.Equal(rekey1.AsSpan().ToArray(), rekey1Again.AsSpan().ToArray());
        Assert.NotEqual(rekey1.AsSpan().ToArray(), rekey2.AsSpan().ToArray());
    }

    /// <summary>
    /// Chained ratcheting must not cycle back to an earlier generation within a small number of
    /// steps (pre/post-key isolation) — each generation is independent key material.
    /// </summary>
    [Fact]
    public void DeriveRekeySecret_WhenChained_YieldsDistinctGenerations()
    {
        const int generations = 16;
        Bytes32 current = RandomNonce(9);
        System.Collections.Generic.HashSet<string> seen = [];
        seen.Add(Convert.ToHexString(current.AsSpan()));

        for (int i = 0; i < generations; i++)
        {
            current = HandshakeX25519.DeriveRekeySecret(current);
            string hex = Convert.ToHexString(current.AsSpan());
            Assert.True(seen.Add(hex), $"Rekey generation {i} collided with a previous generation's secret.");
        }
    }
}
