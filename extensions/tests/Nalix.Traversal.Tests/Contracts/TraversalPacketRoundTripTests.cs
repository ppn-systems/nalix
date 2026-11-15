// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Protocols;
using Nalix.Traversal.Packets;

namespace Nalix.Traversal.Tests.Contracts;

/// <summary>
/// Verifies the NAT traversal wire packets keep their fields and opcodes across a serialize/deserialize round trip.
/// </summary>
public sealed class TraversalPacketRoundTripTests
{
    [Fact]
    public void Deserialize_WhenPeerSignalWasSerialized_PreservesEveryField()
    {
        PeerSignal source = new()
        {
            Type = SignalType.CandidateOffer,
            Port = 54321,
            TargetPeerId = 0x1122334455667788,
            AddressHigh = 0x0102030405060708,
            AddressLow = 0x8877665544332211,
        };

        PeerSignal result = PeerSignal.Deserialize(source.Serialize());

        Assert.Equal(SignalType.CandidateOffer, result.Type);
        Assert.Equal((ushort)54321, result.Port);
        Assert.Equal(0x1122334455667788UL, result.TargetPeerId);
        Assert.Equal(0x0102030405060708UL, result.AddressHigh);
        Assert.Equal(0x8877665544332211UL, result.AddressLow);
        Assert.Equal((ushort)ProtocolOpCode.TRAVERSAL_PEER_SIGNAL, result.Header.OpCode);
    }

    [Fact]
    public void Deserialize_WhenNatProbeWasSerialized_PreservesPeerIdAndOpCode()
    {
        NatProbe result = NatProbe.Deserialize(new NatProbe { PeerId = 77UL }.Serialize());

        Assert.Equal(77UL, result.PeerId);
        Assert.Equal((ushort)ProtocolOpCode.TRAVERSAL_NAT_PROBE, result.Header.OpCode);
    }

    [Fact]
    public void Deserialize_WhenNatProbeAckWasSerialized_PreservesPeerId()
    {
        NatProbeAck result = NatProbeAck.Deserialize(new NatProbeAck { PeerId = 91UL }.Serialize());

        Assert.Equal(91UL, result.PeerId);
        Assert.Equal((ushort)ProtocolOpCode.TRAVERSAL_NAT_PROBE_ACK, result.Header.OpCode);
    }

    [Fact]
    public void Deserialize_WhenReflectorInitWasSerialized_PreservesTargetPeerId()
    {
        ReflectorInit result = ReflectorInit.Deserialize(new ReflectorInit { TargetPeerId = 12UL }.Serialize());

        Assert.Equal(12UL, result.TargetPeerId);
        Assert.Equal((ushort)ProtocolOpCode.TRAVERSAL_REFLECTOR_INIT, result.Header.OpCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Deserialize_WhenReflectorAllocatedWasSerialized_PreservesTokenAndSuccess(bool success)
    {
        ReflectorAllocated result = ReflectorAllocated.Deserialize(
            new ReflectorAllocated { ReflectorToken = 0xCAFEUL, Success = success }.Serialize());

        Assert.Equal(0xCAFEUL, result.ReflectorToken);
        Assert.Equal(success, result.Success);
    }
}
