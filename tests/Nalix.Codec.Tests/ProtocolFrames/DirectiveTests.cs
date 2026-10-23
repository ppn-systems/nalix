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

public sealed class DirectiveTests
{
    [Fact]
    public void Initialize_WhenCalledWithAllArguments_UpdatesPublicProperties()
    {
        Directive packet = new();

        packet.Initialize(
            type: ControlType.REDIRECT,
            reason: ProtocolReason.REDIRECT,
            action: ProtocolAdvice.RECONNECT,
            sequenceId: 99,
            flags: PacketFlags.SYSTEM,
            controlFlags: ControlFlags.HAS_REDIRECT | ControlFlags.IS_TRANSIENT,
            arg0: 1000,
            arg1: 2000,
            arg2: 33);
        PacketHeader h3 = packet.Header;
        h3.OpCode = 77;
        packet.Header = h3;

        Assert.Equal((ushort)77, packet.Header.OpCode);
        Assert.Equal(ControlType.REDIRECT, packet.Type);
        Assert.Equal(ProtocolReason.REDIRECT, packet.Reason);
        Assert.Equal(ProtocolAdvice.RECONNECT, packet.Action);
        Assert.Equal(99u, packet.Header.SequenceId);
        Assert.Equal(ControlFlags.HAS_REDIRECT | ControlFlags.IS_TRANSIENT, packet.Control);
        Assert.Equal(1000u, packet.Arg0);
        Assert.Equal(2000u, packet.Arg1);
        Assert.Equal((ushort)33, packet.Arg2);
        Assert.Equal(PacketPriority.HIGH, packet.Header.Priority);
        Assert.True(packet.Header.Flags.HasFlag(PacketFlags.NONE));
    }

    [Fact]
    public void Length_WhenPacketIsFixedSize_MatchesSerializedBytes()
    {
        Directive packet = new();
        packet.Initialize(
            type: ControlType.REDIRECT,
            reason: ProtocolReason.REDIRECT,
            action: ProtocolAdvice.RECONNECT,
            sequenceId: 99,
            flags: PacketFlags.SYSTEM,
            controlFlags: ControlFlags.HAS_REDIRECT | ControlFlags.IS_TRANSIENT,
            arg0: 1000,
            arg1: 2000,
            arg2: 33);
        PacketHeader h5 = packet.Header;
        h5.OpCode = 77;
        packet.Header = h5;

        byte[] bytes = packet.Serialize();

        Assert.Equal(Directive.Size, packet.Length);
        Assert.Equal(Directive.Size, bytes.Length);
    }

    [Fact]
    public void Initialize_WhenOpCodeIsOmitted_KeepsDirectiveOpCode()
    {
        Directive packet = new();
        packet.Initialize(ControlType.REDIRECT, ProtocolReason.THROTTLED, ProtocolAdvice.SLOW_DOWN, sequenceId: 9, controlFlags: ControlFlags.SLOW_DOWN, arg0: 1, arg1: 2, arg2: 3);

        Assert.Equal((ushort)ProtocolOpCode.SYSTEM_DIRECTIVE, packet.Header.OpCode);
        Assert.Equal(ControlType.REDIRECT, packet.Type);
        Assert.Equal(ProtocolReason.THROTTLED, packet.Reason);
        Assert.Equal(ProtocolAdvice.SLOW_DOWN, packet.Action);
        Assert.Equal(ControlFlags.SLOW_DOWN, packet.Control);
        Assert.Equal(PacketPriority.HIGH, packet.Header.Priority);
    }

    [Fact]
    public void ResetForPool_WhenInitialized_ClearsAllPayloadFields()
    {
        Directive directive = new();
        directive.Initialize(
            type: ControlType.NOTICE,
            reason: ProtocolReason.INTERNAL_ERROR,
            action: ProtocolAdvice.RETRY,
            sequenceId: 456,
            flags: PacketFlags.COMPRESSED,
            controlFlags: ControlFlags.IS_TRANSIENT,
            arg0: 1000,
            arg1: 2000,
            arg2: 3);

        directive.ResetForPool();

        Assert.Equal(Directive.StaticOpCode, directive.Header.OpCode);
        Assert.Equal(0u, directive.Header.SequenceId);
        Assert.Equal(PacketFlags.SYSTEM, directive.Header.Flags);
        Assert.Equal(PacketPriority.HIGH, directive.Header.Priority);
        Assert.Equal(ControlType.NONE, directive.Type);
        Assert.Equal(ProtocolReason.NONE, directive.Reason);
        Assert.Equal(ProtocolAdvice.NONE, directive.Action);
        Assert.Equal(ControlFlags.NONE, directive.Control);
        Assert.Equal(0u, directive.Arg0);
        Assert.Equal(0u, directive.Arg1);
        Assert.Equal((ushort)0, directive.Arg2);
    }
}
