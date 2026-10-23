// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Runtime.Groups;
using Xunit;

namespace Nalix.Network.Tests.Connections;

[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketGroupBroadcastTests : IDisposable
{
    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private sealed class TrackingStubWebSocket : WebSocket
    {
        public readonly ConcurrentQueue<byte[]> SentBuffers = new();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            byte[] copy = new byte[buffer.Count];
            Array.Copy(buffer.Array!, buffer.Offset, copy, 0, buffer.Count);
            SentBuffers.Enqueue(copy);
            return Task.CompletedTask;
        }
    }

    private readonly struct TestSender : IConnectionSender<List<ulong>>
    {
        public ValueTask SendAsync(IConnection connection, ref List<ulong> state, CancellationToken ct)
        {
            lock (state)
            {
                state.Add(connection.ConnectionId);
            }
            return ValueTask.CompletedTask;
        }
    }

    private readonly StubOpCodeExtractor _extractor = new();

    public void Dispose()
    {
        Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
    }

    [Fact]
    public async Task BroadcastAsync_WithWebSocketConnections_DeliversToAll()
    {
        using ConnectionHub hub = new();
        using TrackingStubWebSocket socket1 = new();
        using TrackingStubWebSocket socket2 = new();
        using WebSocketConnection ws1 = new(socket1, _extractor, new IPEndPoint(IPAddress.Loopback, 8081));
        using WebSocketConnection ws2 = new(socket2, _extractor, new IPEndPoint(IPAddress.Loopback, 8082));

        hub.RegisterConnection(ws1);
        hub.RegisterConnection(ws2);

        List<ulong> receivedIds = [];
        TestSender sender = default;

        await hub.BroadcastAsync(receivedIds, sender);

        receivedIds.Should().HaveCount(2);
        receivedIds.Should().Contain(ws1.ConnectionId);
        receivedIds.Should().Contain(ws2.ConnectionId);
    }

    [Fact]
    public async Task MulticastAsync_WithGroup_DeliversOnlyToGroupMembers()
    {
        using ConnectionHub hub = new();
        InMemoryGroupStore groupStore = new();

        using TrackingStubWebSocket socket1 = new();
        using TrackingStubWebSocket socket2 = new();
        using TrackingStubWebSocket socket3 = new();

        using WebSocketConnection ws1 = new(socket1, _extractor, new IPEndPoint(IPAddress.Loopback, 8081));
        using WebSocketConnection ws2 = new(socket2, _extractor, new IPEndPoint(IPAddress.Loopback, 8082));
        using WebSocketConnection ws3 = new(socket3, _extractor, new IPEndPoint(IPAddress.Loopback, 8083));

        hub.RegisterConnection(ws1);
        hub.RegisterConnection(ws2);
        hub.RegisterConnection(ws3);

        await groupStore.AddToGroupAsync("room1", ws1);
        await groupStore.AddToGroupAsync("room1", ws2);

        List<ulong> receivedIds = [];
        TestSender sender = default;

        await hub.MulticastAsync(groupStore, "room1", receivedIds, sender);

        receivedIds.Should().HaveCount(2);
        receivedIds.Should().Contain(ws1.ConnectionId);
        receivedIds.Should().Contain(ws2.ConnectionId);
        receivedIds.Should().NotContain(ws3.ConnectionId);
    }

    [Fact]
    public async Task MulticastExceptAsync_WithExcludedConnection_SkipsExcludedConnection()
    {
        using ConnectionHub hub = new();
        InMemoryGroupStore groupStore = new();

        using TrackingStubWebSocket socket1 = new();
        using TrackingStubWebSocket socket2 = new();
        using TrackingStubWebSocket socket3 = new();

        using WebSocketConnection ws1 = new(socket1, _extractor, new IPEndPoint(IPAddress.Loopback, 8081));
        using WebSocketConnection ws2 = new(socket2, _extractor, new IPEndPoint(IPAddress.Loopback, 8082));
        using WebSocketConnection ws3 = new(socket3, _extractor, new IPEndPoint(IPAddress.Loopback, 8083));

        hub.RegisterConnection(ws1);
        hub.RegisterConnection(ws2);
        hub.RegisterConnection(ws3);

        await groupStore.AddToGroupAsync("room1", ws1);
        await groupStore.AddToGroupAsync("room1", ws2);
        await groupStore.AddToGroupAsync("room1", ws3);

        List<ulong> receivedIds = [];
        TestSender sender = default;

        await hub.MulticastExceptAsync(groupStore, "room1", ws2, receivedIds, sender);

        receivedIds.Should().HaveCount(2);
        receivedIds.Should().Contain(ws1.ConnectionId);
        receivedIds.Should().Contain(ws3.ConnectionId);
        receivedIds.Should().NotContain(ws2.ConnectionId);
    }

    [Fact]
    public async Task BroadcastAsync_WithMixedTcpAndWebSocket_DeliversToBoth()
    {
        using ConnectionHub hub = new();
        using TrackingStubWebSocket socket1 = new();
        using WebSocketConnection ws = new(socket1, _extractor, new IPEndPoint(IPAddress.Loopback, 8081));

        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Task<Socket> acceptTask = Task.Run(listener.Accept);
        using Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);
        using Socket serverSocket = await acceptTask;

        using Connection tcpConn = new(serverSocket, _extractor);

        hub.RegisterConnection(ws);
        hub.RegisterConnection(tcpConn);

        List<ulong> receivedIds = [];
        TestSender sender = default;

        await hub.BroadcastAsync(receivedIds, sender);

        receivedIds.Should().HaveCount(2);
        receivedIds.Should().Contain(ws.ConnectionId);
        receivedIds.Should().Contain(tcpConn.ConnectionId);
    }

    [Fact]
    public async Task MulticastAsync_WhenConnectionDisposed_SkipsWithoutError()
    {
        using ConnectionHub hub = new();
        InMemoryGroupStore groupStore = new();

        using TrackingStubWebSocket socket1 = new();
        using TrackingStubWebSocket socket2 = new();

        WebSocketConnection ws1 = new(socket1, _extractor, new IPEndPoint(IPAddress.Loopback, 8081));
        using WebSocketConnection ws2 = new(socket2, _extractor, new IPEndPoint(IPAddress.Loopback, 8082));

        hub.RegisterConnection(ws1);
        hub.RegisterConnection(ws2);

        await groupStore.AddToGroupAsync("room1", ws1);
        await groupStore.AddToGroupAsync("room1", ws2);

        // Dispose ws1 before broadcast
        ws1.Dispose();

        List<ulong> receivedIds = [];
        TestSender sender = default;

        await hub.MulticastAsync(groupStore, "room1", receivedIds, sender);

        receivedIds.Should().ContainSingle();
        receivedIds.Should().Contain(ws2.ConnectionId);
    }

    [Fact]
    public async Task ConnectionUnregistered_WithoutObserver_LeavesConnectionInGroupStore()
    {
        // Without GroupMembershipObserver wired up, ConnectionHub.ConnectionUnregistered
        // has no subscriber that cleans up group membership, so a disconnected
        // connection stays in its groups forever (stale reference).
        using ConnectionHub hub = new();
        InMemoryGroupStore groupStore = new();

        using TrackingStubWebSocket socket1 = new();
        WebSocketConnection ws1 = new(socket1, _extractor, new IPEndPoint(IPAddress.Loopback, 8081));

        hub.RegisterConnection(ws1);
        await groupStore.AddToGroupAsync("room1", ws1);

        ws1.Dispose(); // triggers ConnectionClosed -> hub unregisters -> fires ConnectionUnregistered

        groupStore.GetGroupMembers("room1").Should().Contain(ws1,
            "no observer is subscribed, so nothing removes the connection from its groups");
    }

    [Fact]
    public async Task ConnectionUnregistered_WithObserver_RemovesConnectionFromGroupStore()
    {
        using ConnectionHub hub = new();
        InMemoryGroupStore groupStore = new();
        using GroupMembershipObserver observer = new(hub, groupStore);

        using TrackingStubWebSocket socket1 = new();
        WebSocketConnection ws1 = new(socket1, _extractor, new IPEndPoint(IPAddress.Loopback, 8081));

        hub.RegisterConnection(ws1);
        await groupStore.AddToGroupAsync("room1", ws1);

        ws1.Dispose(); // triggers ConnectionClosed -> hub unregisters -> observer removes from groups

        groupStore.GetGroupMembers("room1").Should().BeEmpty(
            "GroupMembershipObserver must remove the connection from all groups on disconnect");
    }

    [Fact]
    public async Task MulticastAsync_WithEmptyGroup_ReturnsImmediately()
    {
        using ConnectionHub hub = new();
        InMemoryGroupStore groupStore = new();

        List<ulong> receivedIds = [];
        TestSender sender = default;

        await hub.MulticastAsync(groupStore, "nonexistent", receivedIds, sender);

        receivedIds.Should().BeEmpty();
    }

#pragma warning disable CS0618 // Type or member is obsolete
    [Fact]
    public async Task BroadcastWhereAsync_WithPredicate_DeliversOnlyToMatchingWebSocketConnections()
    {
        using ConnectionHub hub = new();
        using TrackingStubWebSocket socket1 = new();
        using TrackingStubWebSocket socket2 = new();

        using WebSocketConnection ws1 = new(socket1, _extractor, new IPEndPoint(IPAddress.Loopback, 8081));
        using WebSocketConnection ws2 = new(socket2, _extractor, new IPEndPoint(IPAddress.Loopback, 8082));

        ws1.UserId = "user1";
        ws2.UserId = "user2";

        hub.RegisterConnection(ws1);
        hub.RegisterConnection(ws2);

        byte[] payload = [1, 2, 3, 4];
        await hub.BroadcastWhereAsync(payload, conn => conn.UserId == "user1");

        socket1.SentBuffers.Should().ContainSingle();
        socket2.SentBuffers.Should().BeEmpty();
    }
#pragma warning restore CS0618
}
