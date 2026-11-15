// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security.Hashing;
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
    public void TunnelProof_WhenLevelIsNone_IsAllowed()
    {
        PacketHandler<IPacket> handler = DispatchProbe.ResolveHandler(CreateHandler(new TunnelRegistry()), (ushort)ProtocolOpCode.TUNNEL_PROOF);

        bool allowed = DispatchProbe.CanExecute(handler, new TunnelProof(), new FakeConnection { Level = PermissionLevel.NONE }, out ProtocolReason reason);

        Assert.True(allowed);
        Assert.Equal(ProtocolReason.NONE, reason);
    }

    [Fact]
    public async Task TunnelReady_WhenObserverReplaysObservedToken_DoesNotResolvePendingTunnel()
    {
        // An on-path observer of the provider's data connection learns the token. Presenting it on its own
        // connection must not claim the tunnel: the token alone must never be a bearer credential.
        TunnelRegistry registry = new();
        (Task<IConnection> pending, Bytes32 token) = registry.Register();
        FakeConnection observer = new();

        await CreateHandler(registry).HandleReadyAsync(CreateContext(token, observer).context);

        Assert.False(pending.IsCompleted);
        Assert.True(observer.Disconnected);
    }

    [Fact]
    public async Task TunnelReady_WhenTunnelIdIsPending_SendsChallengeWithoutResolving()
    {
        TunnelRegistry registry = new();
        (Task<IConnection> pending, Bytes32 token) = registry.Register();
        FakeConnection dataConnection = new();
        (FakePacketContext<TunnelReady> context, FakePacketSender sender) = CreateContext(TunnelRegistry.ComputeTunnelId(token), dataConnection);

        await CreateHandler(registry).HandleReadyAsync(context);

        Assert.False(pending.IsCompleted);
        Assert.False(dataConnection.Disconnected);
        TunnelChallenge challenge = Assert.IsType<TunnelChallenge>(Assert.Single(sender.Sent));
        Assert.NotEqual(default, challenge.Nonce);
    }

    [Fact]
    public async Task TunnelProof_WhenMacIsValid_ResolvesPendingTunnelWithConnection()
    {
        TunnelRegistry registry = new();
        (Task<IConnection> pending, Bytes32 token) = registry.Register();
        FakeConnection dataConnection = new();
        Bytes32 nonce = await ChallengeAsync(registry, token, dataConnection);

        await CreateHandler(registry).HandleProofAsync(CreateProofContext(ComputeMac(token, nonce), dataConnection));

        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Same(dataConnection, pending.Result);
        Assert.False(dataConnection.Disconnected);
    }

    [Fact]
    public async Task TunnelProof_WhenMacIsInvalid_DisconnectsWithoutResolving()
    {
        TunnelRegistry registry = new();
        (Task<IConnection> pending, Bytes32 token) = registry.Register();
        FakeConnection dataConnection = new();
        await ChallengeAsync(registry, token, dataConnection);

        await CreateHandler(registry).HandleProofAsync(CreateProofContext(new Bytes32(new byte[32]), dataConnection));

        Assert.False(pending.IsCompleted);
        Assert.True(dataConnection.Disconnected);
        Assert.Equal("Invalid tunnel proof", dataConnection.DisconnectReason);
    }

    [Fact]
    public async Task TunnelProof_WhenReplayedFromAnotherConnection_DisconnectsWithoutResolving()
    {
        // The observer relays a valid proof, but it answers a nonce that was issued to a different connection.
        TunnelRegistry registry = new();
        (Task<IConnection> pending, Bytes32 token) = registry.Register();
        FakeConnection legitimate = new();
        Bytes32 legitimateNonce = await ChallengeAsync(registry, token, legitimate);
        Bytes32 proof = ComputeMac(token, legitimateNonce);

        FakeConnection observer = new();
        await ChallengeAsync(registry, token, observer);
        await CreateHandler(registry).HandleProofAsync(CreateProofContext(proof, observer));

        Assert.False(pending.IsCompleted);
        Assert.True(observer.Disconnected);
    }

    [Fact]
    public async Task TunnelProof_WhenNoChallengeWasIssued_Disconnects()
    {
        TunnelRegistry registry = new();
        (Task<IConnection> pending, Bytes32 token) = registry.Register();
        FakeConnection dataConnection = new();

        await CreateHandler(registry).HandleProofAsync(CreateProofContext(ComputeMac(token, default), dataConnection));

        Assert.False(pending.IsCompleted);
        Assert.True(dataConnection.Disconnected);
    }

    [Fact]
    public async Task TunnelProof_WhenSentTwiceOnSameConnection_SecondDisconnects()
    {
        TunnelRegistry registry = new();
        (_, Bytes32 token) = registry.Register();
        FakeConnection dataConnection = new();
        Bytes32 nonce = await ChallengeAsync(registry, token, dataConnection);
        DataConnectionHandler handler = CreateHandler(registry);
        await handler.HandleProofAsync(CreateProofContext(ComputeMac(token, nonce), dataConnection));

        // The first proof resolved the tunnel; the connection is now the consumer's and must not be re-authorized.
        FakeConnection replay = new();
        await handler.HandleProofAsync(CreateProofContext(ComputeMac(token, nonce), replay));

        Assert.True(replay.Disconnected);
    }

    [Fact]
    public async Task TunnelReady_WhenTunnelIdIsReused_DisconnectsSecondConnection()
    {
        TunnelRegistry registry = new();
        (_, Bytes32 token) = registry.Register();
        FakeConnection first = new();
        Bytes32 nonce = await ChallengeAsync(registry, token, first);
        await CreateHandler(registry).HandleProofAsync(CreateProofContext(ComputeMac(token, nonce), first));
        FakeConnection replay = new();

        await CreateHandler(registry).HandleReadyAsync(CreateContext(TunnelRegistry.ComputeTunnelId(token), replay).context);

        Assert.True(replay.Disconnected);
    }

    [Fact]
    public async Task TunnelReady_WhenTunnelIdIsUnknown_DisconnectsWithoutChallenge()
    {
        TunnelRegistry registry = new();
        FakeConnection dataConnection = new();
        (FakePacketContext<TunnelReady> context, FakePacketSender sender) = CreateContext(new Bytes32(new byte[32]), dataConnection);

        await CreateHandler(registry).HandleReadyAsync(context);

        Assert.True(dataConnection.Disconnected);
        Assert.Equal("Invalid tunnel token", dataConnection.DisconnectReason);
        Assert.Empty(sender.Sent);
    }

    private static async Task<Bytes32> ChallengeAsync(TunnelRegistry registry, Bytes32 token, FakeConnection dataConnection)
    {
        (FakePacketContext<TunnelReady> context, FakePacketSender sender) = CreateContext(TunnelRegistry.ComputeTunnelId(token), dataConnection);
        await CreateHandler(registry).HandleReadyAsync(context);
        return Assert.IsType<TunnelChallenge>(Assert.Single(sender.Sent)).Nonce;
    }

    private static Bytes32 ComputeMac(Bytes32 token, Bytes32 nonce)
    {
        Span<byte> mac = stackalloc byte[Bytes32.Size];
        HmacKeccak256.Compute(token.AsSpan(), nonce.AsSpan(), mac);
        return new Bytes32(mac);
    }

    private static DataConnectionHandler CreateHandler(TunnelRegistry registry) => new(registry, NullLogger.Instance);

    private static (FakePacketContext<TunnelReady> context, FakePacketSender sender) CreateContext(Bytes32 tunnelId, FakeConnection connection)
    {
        FakePacketSender sender = new();
        return (new FakePacketContext<TunnelReady>(new TunnelReady { Token = tunnelId }, connection, sender), sender);
    }

    private static FakePacketContext<TunnelProof> CreateProofContext(Bytes32 mac, FakeConnection connection) =>
        new(new TunnelProof { Mac = mac }, connection, new FakePacketSender());
}
