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

namespace Nalix.Codec.Tests.ProtocolFrames;

public sealed class ControlTests
{
    [Fact]
    public void Initialize_WhenCalledWithAllArguments_UpdatesPublicProperties()
    {
        Control packet = new();

        packet.Initialize(ControlType.PING, 42, PacketFlags.SYSTEM, ProtocolReason.TIMEOUT);
        PacketHeader h = packet.Header;
        h.OpCode = 123;
        packet.Header = h;

        Assert.Equal((ushort)123, packet.Header.OpCode);
        Assert.Equal(ControlType.PING, packet.Type);
        Assert.Equal(42u, packet.Header.SequenceId);
        Assert.Equal(ProtocolReason.TIMEOUT, packet.Reason);
        Assert.True(packet.Header.Flags.HasFlag(PacketFlags.NONE));
        Assert.Equal(PacketPriority.HIGH, packet.Header.Priority);
    }

    [Fact]
    public void ResetForPool_WhenInitialized_RestoresControlDefaults()
    {
        Control packet = new();
        packet.Initialize(ControlType.ERROR, 7, PacketFlags.SYSTEM, ProtocolReason.INTERNAL_ERROR);
        PacketHeader h2 = packet.Header;
        h2.OpCode = 555;
        packet.Header = h2;
        packet.Header = new PacketHeader { Flags = PacketFlags.SYSTEM };

        packet.ResetForPool();

        Assert.Equal(ControlType.NONE, packet.Type);
        Assert.Equal(ProtocolReason.NONE, packet.Reason);
        Assert.Equal(0u, packet.Header.SequenceId);
        Assert.Equal(PacketPriority.HIGH, packet.Header.Priority);
        Assert.Equal(PacketFlags.SYSTEM, packet.Header.Flags);
    }

    [Fact]
    public void Length_WhenPacketIsFixedSize_MatchesSerializedBytes()
    {
        Control packet = new();
        packet.Initialize(ControlType.PING, 42, PacketFlags.SYSTEM, ProtocolReason.TIMEOUT);
        PacketHeader h4 = packet.Header;
        h4.OpCode = 123;
        packet.Header = h4;

        byte[] bytes = packet.Serialize();

        Assert.Equal(Control.Size, packet.Length);
        Assert.Equal(Control.Size, bytes.Length);
    }

    [Fact]
    public void Initialize_WhenOpCodeIsOmitted_UpdatesCoreProperties()
    {
        Control packet = new();
        packet.Initialize(ControlType.PING, sequenceId: 44, flags: PacketFlags.SYSTEM, reasonCode: ProtocolReason.TIMEOUT);

        Assert.Equal(ControlType.PING, packet.Type);
        Assert.Equal(44u, packet.Header.SequenceId);
        Assert.Equal(ProtocolReason.TIMEOUT, packet.Reason);
    }
}
