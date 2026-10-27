// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Observability;
using Nalix.Observability.Contracts;
using NSubstitute;

namespace Nalix.Integration.Tests.Observability;

/// <summary>
/// Verifies the access decisions made by <see cref="ObservabilityAccessHandlers"/>.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class ObservabilityAccessHandlersTests : IDisposable
{
    private readonly ObservabilityKeyScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task HandleAsync_WhenStageIsNotRequest_ReturnsMalformedPacket()
    {
        Bytes32 key = _scope.WriteAndLoadRandomKey();
        using ObservabilityAccess request = PacketBase<ObservabilityAccess>.Create();
        request.Initialize(ObservabilityAccessStage.RESPONSE, accessKey: key);
        IConnection connection = Substitute.For<IConnection>();

        using ObservabilityAccess response = await ObservabilityAccessHandlers.HandleAsync(CreateContext(request, connection));

        Assert.Equal(ObservabilityAccessStage.RESPONSE, response.Stage);
        Assert.Equal(ProtocolReason.MALFORMED_PACKET, response.Reason);
        Assert.Equal(PermissionLevel.NONE, connection.Level);
    }

    [Fact]
    public async Task HandleAsync_WhenAccessKeyIsZero_ReturnsMalformedPacket()
    {
        _ = _scope.WriteAndLoadRandomKey();
        using ObservabilityAccess request = PacketBase<ObservabilityAccess>.Create();
        request.Initialize(ObservabilityAccessStage.REQUEST, accessKey: Bytes32.Zero);
        IConnection connection = Substitute.For<IConnection>();

        using ObservabilityAccess response = await ObservabilityAccessHandlers.HandleAsync(CreateContext(request, connection));

        Assert.Equal(ProtocolReason.MALFORMED_PACKET, response.Reason);
        Assert.Equal(PermissionLevel.NONE, response.AccessLevel);
        Assert.Equal(PermissionLevel.NONE, connection.Level);
    }

    [Fact]
    public async Task HandleAsync_WhenAccessKeyDoesNotMatch_ReturnsUnauthorized()
    {
        _ = _scope.WriteAndLoadRandomKey();
        Bytes32 wrongKey = new(RandomNumberGenerator.GetBytes(Bytes32.Size));
        using ObservabilityAccess request = PacketBase<ObservabilityAccess>.Create();
        request.Initialize(ObservabilityAccessStage.REQUEST, accessKey: wrongKey);
        IConnection connection = Substitute.For<IConnection>();

        using ObservabilityAccess response = await ObservabilityAccessHandlers.HandleAsync(CreateContext(request, connection));

        Assert.Equal(ObservabilityAccessStage.RESPONSE, response.Stage);
        Assert.Equal(ProtocolReason.UNAUTHORIZED, response.Reason);
        Assert.Equal(PermissionLevel.NONE, response.AccessLevel);
        Assert.Equal(PermissionLevel.NONE, connection.Level);
    }

    [Fact]
    public async Task HandleAsync_WhenAccessKeyMatches_GrantsSupervisorLevel()
    {
        Bytes32 key = _scope.WriteAndLoadRandomKey();
        using ObservabilityAccess request = PacketBase<ObservabilityAccess>.Create();
        request.Initialize(ObservabilityAccessStage.REQUEST, accessKey: key);
        IConnection connection = Substitute.For<IConnection>();

        using ObservabilityAccess response = await ObservabilityAccessHandlers.HandleAsync(CreateContext(request, connection));

        Assert.Equal(ObservabilityAccessStage.RESPONSE, response.Stage);
        Assert.Equal(ProtocolReason.NONE, response.Reason);
        Assert.Equal(PermissionLevel.SUPERVISOR, response.AccessLevel);
        Assert.Equal(PermissionLevel.SUPERVISOR, connection.Level);
    }

    private static IPacketContext<ObservabilityAccess> CreateContext(ObservabilityAccess packet, IConnection connection)
    {
        IPacketContext<ObservabilityAccess> context = Substitute.For<IPacketContext<ObservabilityAccess>>();
        context.Packet.Returns(packet);
        context.Connection.Returns(connection);
        return context;
    }
}
