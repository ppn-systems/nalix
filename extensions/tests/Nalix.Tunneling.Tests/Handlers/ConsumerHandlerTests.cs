// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Tunneling.Handlers;
using Nalix.Tunneling.Tests.TestSupport;

namespace Nalix.Tunneling.Tests.Handlers;

public sealed class ConsumerHandlerTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(PermissionLevel.NONE)]
    [InlineData(PermissionLevel.GUEST)]
    public void TunnelConnect_WhenLevelBelowUser_ReturnsUnauthorized(PermissionLevel level)
    {
        PacketHandler<IPacket> handler = DispatchProbe.ResolveHandler(CreateHandler(new Harness()), (ushort)ProtocolOpCode.TUNNEL_CONNECT);

        bool allowed = DispatchProbe.CanExecute(handler, new TunnelConnect { ChannelId = 1 }, new FakeConnection { Level = level }, out ProtocolReason reason);

        Assert.False(allowed);
        Assert.Equal(ProtocolReason.UNAUTHORIZED, reason);
    }

    [Fact]
    public void TunnelConnect_WhenLevelIsUser_IsAllowed()
    {
        PacketHandler<IPacket> handler = DispatchProbe.ResolveHandler(CreateHandler(new Harness()), (ushort)ProtocolOpCode.TUNNEL_CONNECT);

        bool allowed = DispatchProbe.CanExecute(handler, new TunnelConnect { ChannelId = 1 }, new FakeConnection { Level = PermissionLevel.USER }, out _);

        Assert.True(allowed);
    }

    [Fact]
    public async Task TunnelConnect_WhenNoProviderRegistered_AcksChannelNotFound()
    {
        Harness harness = new();
        FakePacketSender consumerSender = new();

        await CreateHandler(harness).HandleConnectAsync(new FakePacketContext<TunnelConnect>(new TunnelConnect { ChannelId = 3 }, new FakeConnection(), consumerSender));

        TunnelConnectAck ack = Assert.IsType<TunnelConnectAck>(Assert.Single(consumerSender.Sent));
        Assert.False(ack.Success);
        Assert.Equal(1, ack.Reason);
    }

    [Fact]
    public async Task TunnelConnect_WhenConsumerAlreadyTunneling_AcksAlreadyTunnelingWithoutRequestingProvider()
    {
        Harness harness = new();
        FakeConnection consumer = new();
        _ = harness.Providers.TryRegister(3, new FakeConnection(), harness.ProviderSender);
        harness.Sessions.Register(consumer.ConnectionId, new TunnelSession(consumer, harness.Options, harness.Sessions));
        FakePacketSender consumerSender = new();

        await CreateHandler(harness).HandleConnectAsync(new FakePacketContext<TunnelConnect>(new TunnelConnect { ChannelId = 3 }, consumer, consumerSender));

        TunnelConnectAck ack = Assert.IsType<TunnelConnectAck>(Assert.Single(consumerSender.Sent));
        Assert.False(ack.Success);
        Assert.Equal(4, ack.Reason);
        Assert.Empty(harness.ProviderSender.Sent);
    }

    [Fact]
    public async Task TunnelConnect_WhenPendingTunnelsReachLimit_AcksTunnelLimitReachedWithoutRequestingProvider()
    {
        Harness harness = new() { Options = { MaxConcurrentTunnels = 1 } };
        _ = harness.Providers.TryRegister(3, new FakeConnection(), harness.ProviderSender);
        Task first = StartConnectAsync(harness, new FakeConnection());
        await Wait.UntilAsync(() => harness.ProviderSender.Sent.Count == 1, s_wait);
        FakePacketSender secondSender = new();

        await CreateHandler(harness).HandleConnectAsync(new FakePacketContext<TunnelConnect>(new TunnelConnect { ChannelId = 3 }, new FakeConnection(), secondSender));

        TunnelConnectAck ack = Assert.IsType<TunnelConnectAck>(Assert.Single(secondSender.Sent));
        Assert.False(ack.Success);
        Assert.Equal(3, ack.Reason);
        Assert.Single(harness.ProviderSender.Sent);
        await CompleteConnectAsync(harness, first);
    }

    [Fact]
    public async Task TunnelConnect_WhenSessionStartFails_DisconnectsProviderDataConnection()
    {
        Harness harness = new();
        _ = harness.Providers.TryRegister(3, new FakeConnection(), harness.ProviderSender);
        FakeConnection providerData = new();
        Task pending = StartConnectAsync(harness, new FakeConnection());
        await Wait.UntilAsync(() => harness.ProviderSender.Sent.Count == 1, s_wait);

        TunnelRequest request = Assert.IsType<TunnelRequest>(harness.ProviderSender.Sent[0]);
        _ = harness.Tunnels.Resolve(request.Token, providerData);
        await pending.WaitAsync(s_wait);

        Assert.True(providerData.Disconnected);
    }

    private static Task StartConnectAsync(Harness harness, FakeConnection consumer)
    {
        FakePacketContext<TunnelConnect> context = new(new TunnelConnect { ChannelId = 3 }, consumer, new FakePacketSender());
        return CreateHandler(harness).HandleConnectAsync(context).AsTask();
    }

    private static async Task CompleteConnectAsync(Harness harness, Task pending)
    {
        TunnelRequest request = Assert.IsType<TunnelRequest>(harness.ProviderSender.Sent[0]);
        _ = harness.Tunnels.Resolve(request.Token, new FakeConnection());
        await pending.WaitAsync(s_wait);
    }

    private static ConsumerHandler CreateHandler(Harness harness) =>
        new(harness.Providers, harness.Tunnels, harness.Sessions, harness.Options, NullLogger.Instance);

    private sealed class Harness
    {
        public ProviderRegistry Providers { get; } = new();
        public TunnelRegistry Tunnels { get; } = new();
        public TunnelSessionRegistry Sessions { get; } = new();
        public TunnelOptions Options { get; } = new();
        public FakePacketSender ProviderSender { get; } = new();
    }
}
