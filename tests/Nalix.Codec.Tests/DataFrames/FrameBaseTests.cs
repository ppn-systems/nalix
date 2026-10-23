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

namespace Nalix.Codec.Tests.DataFrames;

public sealed class FrameBaseTests
{
    static FrameBaseTests()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Theory]
    [MemberData(nameof(FrameTestData.PacketRoundTripCases), MemberType = typeof(FrameTestData))]
    public void Serialize_ThenDeserialize_PreservesPublicState(FrameTestData.PacketRoundTripKind packetKind)
    {
        FrameBase original = FrameTestData.CreateRoundTripPacket(packetKind);

        byte[] bytes = original.Serialize();
        FrameBase deserialized = original switch
        {
            Control => Control.Deserialize(bytes),
            Directive => Directive.Deserialize(bytes),
            _ => throw new InvalidOperationException("Unexpected frame type.")
        };

        FrameTestData.AssertRoundTripPacketEquivalent(packetKind, original, deserialized);
    }

    [Fact]
    public void Deserialize_WhenGivenReadOnlySpan_RestoresAllSignalFrames()
    {
        byte[] controlBytes = FrameTestData.CreateControlPacket().Serialize();
        byte[] directiveBytes = FrameTestData.CreateDirectivePacket().Serialize();

        SessionResume resume = new();
        resume.Initialize(SessionResumeStage.REQUEST, 0UL, ProtocolReason.NONE, Bytes32.Zero, PacketFlags.SYSTEM);
        byte[] sessionResumeBytes = resume.Serialize();

        Control control = Control.Deserialize(controlBytes.AsSpan());
        Directive directive = Directive.Deserialize(directiveBytes.AsSpan());
        SessionResume sessionResume = SessionResume.Deserialize(sessionResumeBytes.AsSpan());

        Assert.Equal(ControlType.PING, control.Type);
        Assert.Equal(ControlType.REDIRECT, directive.Type);
        Assert.Equal(SessionResumeStage.REQUEST, sessionResume.Stage);
    }
}
