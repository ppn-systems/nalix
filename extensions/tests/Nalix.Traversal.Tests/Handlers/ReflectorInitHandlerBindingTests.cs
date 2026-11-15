// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Traversal.Handlers;
using Nalix.Traversal.Packets;
using Nalix.Traversal.Reflector;
using Nalix.Traversal.Tests.TestSupport;
using NSubstitute;

namespace Nalix.Traversal.Tests.Handlers;

/// <summary>
/// Verifies that <see cref="ReflectorInitHandler"/> binds each <see cref="ReflectorAllocated"/> to its request or pair.
/// </summary>
public sealed class ReflectorInitHandlerBindingTests
{
    private const ulong TargetPeerId = 2UL;
    private const ulong NonceFromClient = 0x5EEDUL;

    [Fact]
    public async Task HandleAsync_WhenRequested_ReplyEchoesNonceAndNamesTargetPeer()
    {
        FakeConnection requester = new() { Level = PermissionLevel.USER };
        FakePacketSender sender = new();
        ReflectorInitHandler handler = new(new ReflectorManager());

        await handler.HandleAsync(new FakePacketContext<ReflectorInit>(
            new ReflectorInit { TargetPeerId = TargetPeerId, RequestNonce = NonceFromClient },
            requester,
            sender));

        ReflectorAllocated reply = Assert.IsType<ReflectorAllocated>(Assert.Single(sender.Sent));
        Assert.True(reply.Success);
        Assert.Equal(TargetPeerId, reply.PeerId);
        Assert.Equal(NonceFromClient, reply.RequestNonce);
    }

    [Fact]
    public async Task HandleAsync_WhenTargetIsConnected_SendsNotificationToTarget()
    {
        FakeConnection requester = new() { Level = PermissionLevel.USER };
        FakeConnection target = new("198.51.100.20")
        {
            Level = PermissionLevel.USER,
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            Secret = new Bytes32(Enumerable.Repeat((byte)0x42, 32).ToArray()),
        };
        IConnectionHub hub = Substitute.For<IConnectionHub>();
        _ = hub.GetConnection(TargetPeerId).Returns(target);
        requester.Attributes[ConnectionAttributes.OwnerHub] = hub;
        FakePacketSender sender = new();
        ReflectorInitHandler handler = new(new ReflectorManager());

        await handler.HandleAsync(new FakePacketContext<ReflectorInit>(
            new ReflectorInit { TargetPeerId = TargetPeerId, RequestNonce = NonceFromClient },
            requester,
            sender));

        // The notification is encrypted on the wire, so its PeerId and RequestNonce are covered by the contract round-trip tests.
        Assert.Single(target.FakeTcp.SentMessages);
        Assert.Single(sender.Sent); // the requester still gets its own reply
    }
}
