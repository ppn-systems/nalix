// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;

namespace Nalix.Tunneling.Tests.Contracts;

/// <summary>
/// Verifies the tunnel wire packets keep their fields and opcodes across a serialize/deserialize round trip.
/// </summary>
public sealed class TunnelPacketRoundTripTests
{
    [Fact]
    public void Deserialize_WhenTunnelConnectWasSerialized_PreservesChannelIdAndOpCode()
    {
        TunnelConnect source = new() { ChannelId = 0xBEEF };

        TunnelConnect result = TunnelConnect.Deserialize(source.Serialize());

        Assert.Equal(0xBEEF, result.ChannelId);
        Assert.Equal((ushort)ProtocolOpCode.TUNNEL_CONNECT, result.Header.OpCode);
    }

    [Fact]
    public void Deserialize_WhenTunnelProvideWasSerialized_PreservesChannelIdAndOpCode()
    {
        TunnelProvide source = new() { ChannelId = 7 };

        TunnelProvide result = TunnelProvide.Deserialize(source.Serialize());

        Assert.Equal((ushort)7, result.ChannelId);
        Assert.Equal((ushort)ProtocolOpCode.TUNNEL_PROVIDE, result.Header.OpCode);
    }

    [Fact]
    public void Deserialize_WhenTunnelReadyWasSerialized_PreservesToken()
    {
        byte[] tokenBytes = new byte[Bytes32.Size];
        for (int i = 0; i < tokenBytes.Length; i++)
        {
            tokenBytes[i] = (byte)(i * 3);
        }

        TunnelReady source = new() { Token = new Bytes32(tokenBytes) };

        TunnelReady result = TunnelReady.Deserialize(source.Serialize());

        Assert.Equal(source.Token, result.Token);
        Assert.Equal(tokenBytes, result.Token.ToByteArray());
    }

    [Fact]
    public void Deserialize_WhenTunnelRequestWasSerialized_PreservesToken()
    {
        Bytes32 token = Bytes32.Parse(new string('a', Bytes32.Size * 2));

        TunnelRequest result = TunnelRequest.Deserialize(new TunnelRequest { Token = token }.Serialize());

        Assert.Equal(token, result.Token);
    }

    [Theory]
    [InlineData(true, (byte)0)]
    [InlineData(false, (byte)3)]
    public void Deserialize_WhenConnectAckWasSerialized_PreservesSuccessAndReason(bool success, byte reason)
    {
        TunnelConnectAck result = TunnelConnectAck.Deserialize(
            new TunnelConnectAck { Success = success, Reason = reason }.Serialize());

        Assert.Equal(success, result.Success);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData(true, (byte)0)]
    [InlineData(false, (byte)5)]
    public void Deserialize_WhenProvideAckWasSerialized_PreservesSuccessAndReason(bool success, byte reason)
    {
        TunnelProvideAck result = TunnelProvideAck.Deserialize(
            new TunnelProvideAck { Success = success, Reason = reason }.Serialize());

        Assert.Equal(success, result.Success);
        Assert.Equal(reason, result.Reason);
    }
}
