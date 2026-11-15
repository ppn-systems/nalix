// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Tunneling.Handlers;
using Nalix.Tunneling.Tests.TestSupport;

namespace Nalix.Tunneling.Tests.Handlers;

public sealed class DataConnectionHandlerTests
{
    [Fact]
    public void TunnelReady_WhenLevelIsNone_IsAllowed()
    {
        PacketHandler<IPacket> handler = DispatchProbe.ResolveHandler(CreateHandler(new TunnelRegistry()), (ushort)ProtocolOpCode.TUNNEL_READY);

        bool allowed = DispatchProbe.CanExecute(handler, new TunnelReady(), new FakeConnection { Level = PermissionLevel.NONE }, out ProtocolReason reason);

        Assert.True(allowed);
        Assert.Equal(ProtocolReason.NONE, reason);
    }

    [Fact]
    public async Task TunnelReady_WhenTokenIsPending_ResolvesPendingTunnelWithConnection()
    {
        TunnelRegistry registry = new();
        (Task<IConnection> pending, Bytes32 token) = registry.Register();
        FakeConnection dataConnection = new();

        await CreateHandler(registry).HandleReadyAsync(CreateContext(token, dataConnection));

        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Same(dataConnection, pending.Result);
        Assert.False(dataConnection.Disconnected);
    }

    [Fact]
    public async Task TunnelReady_WhenTokenIsUnknown_DisconnectsWithoutResolving()
    {
        TunnelRegistry registry = new();
        FakeConnection dataConnection = new();

        await CreateHandler(registry).HandleReadyAsync(CreateContext(new Bytes32(new byte[32]), dataConnection));

        Assert.True(dataConnection.Disconnected);
        Assert.Equal("Invalid tunnel token", dataConnection.DisconnectReason);
    }

    [Fact]
    public async Task TunnelReady_WhenTokenIsReused_DisconnectsSecondConnection()
    {
        TunnelRegistry registry = new();
        (_, Bytes32 token) = registry.Register();
        await CreateHandler(registry).HandleReadyAsync(CreateContext(token, new FakeConnection()));
        FakeConnection replay = new();

        await CreateHandler(registry).HandleReadyAsync(CreateContext(token, replay));

        Assert.True(replay.Disconnected);
    }

    private static DataConnectionHandler CreateHandler(TunnelRegistry registry) => new(registry, NullLogger.Instance);

    private static FakePacketContext<TunnelReady> CreateContext(Bytes32 token, FakeConnection connection) =>
        new(new TunnelReady { Token = token }, connection, new FakePacketSender());
}
