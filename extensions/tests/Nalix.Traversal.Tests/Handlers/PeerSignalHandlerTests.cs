// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NSubstitute;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Traversal.Handlers;
using Nalix.Traversal.Packets;
using Nalix.Traversal.Tests.TestSupport;

namespace Nalix.Traversal.Tests.Handlers;

public sealed class PeerSignalHandlerTests
{
    private const string SenderAddress = "203.0.113.7";

    [Fact]
    public void PeerSignal_WhenLevelBelowUser_ReturnsUnauthorized()
    {
        PacketHandler<IPacket> handler = DispatchProbe.ResolveHandler(new PeerSignalHandler(), (ushort)ProtocolOpCode.TRAVERSAL_PEER_SIGNAL);

        bool allowed = DispatchProbe.CanExecute(handler, new PeerSignal(), new FakeConnection { Level = PermissionLevel.NONE }, out ProtocolReason reason);

        Assert.False(allowed);
        Assert.Equal(ProtocolReason.UNAUTHORIZED, reason);
    }

    [Fact]
    public async Task PeerSignal_WhenOfferCarriesSendersObservedAddress_RelaysToTarget()
    {
        (FakeConnection sender, FakeConnection target, FakePacketSender senderSender) = Peers();
        PeerSignal offer = Offer(IPAddress.Parse(SenderAddress), 40000, target.ConnectionId);

        await PeerSignalHandler.HandleAsync(new FakePacketContext<PeerSignal>(offer, sender, senderSender));

        Assert.Single(target.FakeTcp.SentMessages);
        Assert.Empty(senderSender.Sent);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("198.51.100.9")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    public async Task PeerSignal_WhenOfferClaimsAddressOtherThanSenders_DoesNotRelay(string claimed)
    {
        (FakeConnection sender, FakeConnection target, FakePacketSender senderSender) = Peers();
        PeerSignal offer = Offer(IPAddress.Parse(claimed), 9, target.ConnectionId);

        await PeerSignalHandler.HandleAsync(new FakePacketContext<PeerSignal>(offer, sender, senderSender));

        Assert.Empty(target.FakeTcp.SentMessages);
        PeerSignal result = Assert.IsType<PeerSignal>(Assert.Single(senderSender.Sent));
        Assert.Equal(SignalType.Result, result.Type);
    }

    [Fact]
    public async Task PeerSignal_WhenOfferHasPortZero_DoesNotRelay()
    {
        (FakeConnection sender, FakeConnection target, FakePacketSender senderSender) = Peers();
        PeerSignal offer = Offer(IPAddress.Parse(SenderAddress), 0, target.ConnectionId);

        await PeerSignalHandler.HandleAsync(new FakePacketContext<PeerSignal>(offer, sender, senderSender));

        Assert.Empty(target.FakeTcp.SentMessages);
    }

    [Fact]
    public async Task PeerSignal_WhenRequestCarriesAddress_RelaysWithoutThatAddress()
    {
        (FakeConnection sender, FakeConnection target, FakePacketSender senderSender) = Peers();
        PeerSignal request = new()
        {
            Type = SignalType.Request,
            TargetPeerId = target.ConnectionId,
            Port = 9,
        };
        SetAddress(request, IPAddress.Parse("10.0.0.1"));

        await PeerSignalHandler.HandleAsync(new FakePacketContext<PeerSignal>(request, sender, senderSender));

        Assert.Single(target.FakeTcp.SentMessages);
        Assert.Empty(senderSender.Sent);
    }

    private static (FakeConnection Sender, FakeConnection Target, FakePacketSender SenderSender) Peers()
    {
        FakeConnection sender = new(SenderAddress) { Level = PermissionLevel.USER };
        FakeConnection target = new("198.51.100.20")
        {
            Level = PermissionLevel.USER,
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            Secret = new Bytes32(Enumerable.Repeat((byte)0x42, 32).ToArray()),
        };

        IConnectionHub hub = Substitute.For<IConnectionHub>();
        _ = hub.GetConnection(target.ConnectionId).Returns(target);
        sender.Attributes[ConnectionAttributes.OwnerHub] = hub;

        return (sender, target, new FakePacketSender());
    }

    private static PeerSignal Offer(IPAddress address, ushort port, ulong targetId)
    {
        PeerSignal offer = new()
        {
            Type = SignalType.CandidateOffer,
            TargetPeerId = targetId,
            Port = port,
        };
        SetAddress(offer, address);
        return offer;
    }

    private static void SetAddress(PeerSignal packet, IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = address.MapToIPv6().TryWriteBytes(bytes, out _);
        packet.AddressLow = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        packet.AddressHigh = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }
}
