// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Routing;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Tunneling.Handlers;
using Nalix.Tunneling.Tests.TestSupport;

namespace Nalix.Tunneling.Tests.Handlers;

public sealed class ProviderSenderLifetimeTests
{
    private const byte EncryptedFlag = 1 << 2;

    [Fact]
    public async Task TunnelConnect_AfterProviderDispatchReturnedItsContextToPool_StillForwardsEncryptedTunnelRequest()
    {
        // Real pooling path: TUNNEL_PROVIDE is dispatched through PacketDispatchOptions, so the handler's
        // PacketContext (and its PacketSender) is returned to the pool when the handler completes.
        FakeConnection provider = CreateSessionConnection();
        ProviderRegistry providers = new();
        ProviderHandler providerHandler = new(providers, NullLogger.Instance);
        PacketDispatchOptions<IPacket> dispatch = new PacketDispatchOptions<IPacket>().WithHandler(providerHandler);
        Assert.True(dispatch.TryResolveHandler((ushort)ProtocolOpCode.TUNNEL_PROVIDE, out PacketHandler<IPacket> handler));

        await DispatchAsync(dispatch, handler, new TunnelProvide { ChannelId = 3 }, provider);
        Assert.True(providers.TryGetProvider(3, out _), "TUNNEL_PROVIDE did not register the provider.");

        Task connect = RunConnectAsync(providers);
        await Wait.UntilAsync(() => provider.FakeTcp.SentMessages.Count >= 2 || connect.IsFaulted, TimeSpan.FromSeconds(5));

        Assert.False(connect.IsFaulted, $"TUNNEL_CONNECT faulted: {connect.Exception?.GetBaseException().Message}");
        byte[] request = Assert.Single(provider.FakeTcp.SentMessages.Skip(1).Where(m => BitConverter.ToUInt16(m, 0) == (ushort)ProtocolOpCode.TUNNEL_REQUEST));
        Assert.True((request[2] & EncryptedFlag) != 0, "TUNNEL_REQUEST must be encrypted.");
    }

    private static async Task DispatchAsync(PacketDispatchOptions<IPacket> dispatch, PacketHandler<IPacket> handler, IPacket packet, FakeConnection connection)
    {
        MethodInfo execute = typeof(PacketDispatchOptions<IPacket>).GetMethod("ExecuteResolvedHandlerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ValueTask pending = (ValueTask)execute.Invoke(dispatch, [handler, packet, connection, false, CancellationToken.None])!;
        await pending;
    }

    private static FakeConnection CreateSessionConnection()
    {
        Span<byte> secret = stackalloc byte[Bytes32.Size];
        Nalix.Environment.Random.Csprng.Fill(secret);
        return new FakeConnection { Level = PermissionLevel.USER, Algorithm = CipherSuiteType.Chacha20Poly1305, Secret = new Bytes32(secret) };
    }

    private static Task RunConnectAsync(ProviderRegistry providers)
    {
        ConsumerHandler handler = new(providers, new TunnelRegistry(), new TunnelSessionRegistry(), new TunnelOptions(), NullLogger.Instance);
        return handler.HandleConnectAsync(new FakePacketContext<TunnelConnect>(new TunnelConnect { ChannelId = 3 }, new FakeConnection(), new FakePacketSender())).AsTask();
    }
}
