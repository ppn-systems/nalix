// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Tunneling.Handlers;
using Nalix.Tunneling.Tests.TestSupport;

namespace Nalix.Tunneling.Tests.Handlers;

public sealed class ProviderHandlerTests
{
    [Theory]
    [InlineData(PermissionLevel.NONE)]
    [InlineData(PermissionLevel.GUEST)]
    public void TunnelProvide_WhenLevelBelowUser_ReturnsUnauthorized(PermissionLevel level)
    {
        PacketHandler<IPacket> handler = DispatchProbe.ResolveHandler(CreateHandler(new ProviderRegistry()), (ushort)ProtocolOpCode.TUNNEL_PROVIDE);

        bool allowed = DispatchProbe.CanExecute(handler, new TunnelProvide { ChannelId = 1 }, new FakeConnection { Level = level }, out ProtocolReason reason);

        Assert.False(allowed);
        Assert.Equal(ProtocolReason.UNAUTHORIZED, reason);
    }

    [Fact]
    public void TunnelProvide_WhenLevelIsUser_IsAllowed()
    {
        PacketHandler<IPacket> handler = DispatchProbe.ResolveHandler(CreateHandler(new ProviderRegistry()), (ushort)ProtocolOpCode.TUNNEL_PROVIDE);

        bool allowed = DispatchProbe.CanExecute(handler, new TunnelProvide { ChannelId = 1 }, new FakeConnection { Level = PermissionLevel.USER }, out _);

        Assert.True(allowed);
    }

    [Fact]
    public async Task TunnelProvide_WhenChannelOwnedByAnotherConnection_AcksFailureAndKeepsOwner()
    {
        ProviderRegistry registry = new();
        FakeConnection owner = new() { Level = PermissionLevel.USER };
        FakeConnection squatter = new() { Level = PermissionLevel.USER };
        _ = registry.TryRegister(9, owner);
        FakePacketSender squatterSender = new();

        await CreateHandler(registry).HandleProvideAsync(new FakePacketContext<TunnelProvide>(new TunnelProvide { ChannelId = 9 }, squatter, squatterSender));

        TunnelProvideAck ack = Assert.IsType<TunnelProvideAck>(Assert.Single(squatterSender.Sent));
        Assert.False(ack.Success);
        Assert.Equal(1, ack.Reason);
        Assert.True(registry.TryGetProvider(9, out IConnection? connection));
        Assert.Same(owner, connection);
    }

    private static ProviderHandler CreateHandler(ProviderRegistry registry) => new(registry, null);
}
