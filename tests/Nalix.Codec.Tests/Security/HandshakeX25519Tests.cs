// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Text;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Primitives;

namespace Nalix.Codec.Tests.Security;

public sealed class HandshakeX25519Tests
{
    private static Bytes32 Fill(byte value)
    {
        byte[] bytes = Enumerable.Repeat(value, Bytes32.Size).Select(static x => (byte)x).ToArray();
        return new Bytes32(bytes);
    }

    [Fact]
    public void ComposeTranscriptBuffer_WithFourValues_WritesLengthPrefixedSegments()
    {
        Bytes32 cpk = Fill(0x11);
        Bytes32 cnonce = Fill(0x22);
        Bytes32 spk = Fill(0x33);
        Bytes32 snonce = Fill(0x44);

        byte[] buffer = HandshakeX25519.ComposeTranscriptBuffer(cpk, cnonce, spk, snonce);

        Assert.Equal((4 * 4) + (Bytes32.Size * 4), buffer.Length);
        Assert.Equal(Bytes32.Size, BitConverter.ToInt32(buffer, 0));
        Assert.Equal(0x11, buffer[4]);
        Assert.Equal(Bytes32.Size, BitConverter.ToInt32(buffer, 36));
        Assert.Equal(0x22, buffer[40]);
    }

    [Fact]
    public void Proofs_WithSameInputs_AreDeterministicAndDomainSeparated()
    {
        Bytes32 secret = Fill(0xAA);
        Bytes32 transcript = Fill(0xBB);
        Bytes32 cnonce = Fill(0xCC);
        Bytes32 snonce = Fill(0xDD);

        Bytes32 server1 = HandshakeX25519.ComputeServerProof(secret, transcript);
        Bytes32 server2 = HandshakeX25519.ComputeServerProof(secret, transcript);
        Bytes32 client = HandshakeX25519.ComputeClientProof(secret, transcript);
        Bytes32 finish = HandshakeX25519.ComputeServerFinishProof(secret, transcript);
        Bytes32 session1 = HandshakeX25519.DeriveSessionKey(secret, cnonce, snonce, transcript);
        Bytes32 session2 = HandshakeX25519.DeriveSessionKey(secret, cnonce, snonce, transcript);
        Bytes32 sessionDiff = HandshakeX25519.DeriveSessionKey(secret, Fill(0xCE), snonce, transcript);

        Assert.Equal(server1, server2);
        Assert.NotEqual(server1, client);
        Assert.NotEqual(server1, finish);
        Assert.Equal(session1, session2);
        Assert.NotEqual(session1, sessionDiff);
    }
}
