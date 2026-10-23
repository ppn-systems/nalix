// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Codec.Tests.TestSupport;
using Nalix.Codec.Transforms;
using Nalix.Environment.Extensions;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Random;
using System.Text;

namespace Nalix.Codec.Tests.ProtocolFrames.Session;

public sealed class SessionResumeTests
{
    [Fact]
    public void Initialize_WhenOptionalValuesAreOmitted_UsesExpectedDefaults()
    {
        SessionResume packet = new();
        packet.Initialize(SessionResumeStage.REQUEST, 0UL);

        Assert.Equal((ushort)ProtocolOpCode.SESSION_SIGNAL, packet.Header.OpCode);
        Assert.Equal(SessionResumeStage.REQUEST, packet.Stage);
        Assert.Equal(ProtocolReason.NONE, packet.Reason);
        Assert.True(packet.Proof.IsZero);
        Assert.Equal(PacketPriority.URGENT, packet.Header.Priority);
    }

    [Fact]
    public void Serialize_ThenDeserialize_PreservesPublicState()
    {
        SessionResume original = new();
        original.Initialize(
            SessionResumeStage.REQUEST,
            0UL,
            ProtocolReason.NONE,
            Bytes32.Zero,
            PacketFlags.SYSTEM);

        byte[] bytes = original.Serialize();
        SessionResume deserialized = SessionResume.Deserialize(bytes);

        Assert.Equal(original.Header.OpCode, deserialized.Header.OpCode);
        Assert.Equal(original.Stage, deserialized.Stage);
        Assert.Equal(original.SessionToken, deserialized.SessionToken);
        Assert.Equal(original.Reason, deserialized.Reason);
        Assert.Equal(original.Proof, deserialized.Proof);
        Assert.Equal(original.Header.Flags, deserialized.Header.Flags);
        Assert.Equal(original.Header.Priority, deserialized.Header.Priority);
    }

    [Fact]
    public void ResetForPool_WhenContainingData_RestoresDefaults()
    {
        SessionResume packet = new();
        packet.Initialize(
            SessionResumeStage.RESPONSE,
            0UL,
            ProtocolReason.TIMEOUT,
            Bytes32.Zero,
            PacketFlags.SYSTEM);
        PacketHeader h = packet.Header;
        h.Flags = PacketFlags.SYSTEM;
        packet.Header = h;

        packet.ResetForPool();

        Assert.Equal((ushort)ProtocolOpCode.SESSION_SIGNAL, packet.Header.OpCode);
        Assert.Equal(SessionResumeStage.NONE, packet.Stage);
        Assert.Equal(0UL, packet.SessionToken);
        Assert.Equal(ProtocolReason.NONE, packet.Reason);
        Assert.True(packet.Proof.IsZero);
        Assert.Equal(PacketFlags.SYSTEM, packet.Header.Flags);
        Assert.Equal(PacketPriority.URGENT, packet.Header.Priority);
    }

    [Fact]
    public void Length_WhenPacketIsFixedSize_MatchesSerializedLength()
    {
        SessionResume packet = new();
        packet.Initialize(
            SessionResumeStage.REQUEST,
            0UL,
            ProtocolReason.NONE,
            Bytes32.Zero,
            PacketFlags.SYSTEM);

        byte[] bytes = packet.Serialize();

        Assert.Equal(SessionResume.Size, packet.Length);
        Assert.Equal(SessionResume.Size, bytes.Length);
    }
}
