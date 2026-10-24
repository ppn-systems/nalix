// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Xunit;

namespace Nalix.Network.Tests.Connections;

[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketConnectionHubTests : IDisposable
{
    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private sealed class TestStubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private readonly StubOpCodeExtractor _extractor = new();

    public void Dispose()
    {
        Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
    }

    [Fact]
    public void RegisterWebSocketConnection_WhenCalled_IncrementsCountAndAllowsLookup()
    {
        using ConnectionHub hub = new();
        using TestStubWebSocket socket = new();
        using WebSocketConnection connection = new(socket, _extractor, new IPEndPoint(IPAddress.Loopback, 8080));

        hub.RegisterConnection(connection);

        hub.Count.Should().Be(1);
        hub.GetConnection(connection.ConnectionId).Should().BeSameAs(connection);
    }

    [Fact]
    public void UnregisterWebSocketConnection_WhenRegistered_DecrementsCountAndRaisesEvent()
    {
        using ConnectionHub hub = new();
        using TestStubWebSocket socket = new();
        using WebSocketConnection connection = new(socket, _extractor, new IPEndPoint(IPAddress.Loopback, 8080));

        IConnection? observed = null;
        hub.ConnectionUnregistered += c => observed = c;

        hub.RegisterConnection(connection);
        hub.UnregisterConnection(connection);

        hub.Count.Should().Be(0);
        observed.Should().NotBeNull();
        observed!.ConnectionId.Should().Be(connection.ConnectionId);
        hub.GetConnection(connection.ConnectionId).Should().BeNull();
    }

    [Fact]
    public void ListConnections_WithWebSocketConnections_ReturnsThemByEndpoint()
    {
        using ConnectionHub hub = new();
        using TestStubWebSocket socket1 = new();
        using TestStubWebSocket socket2 = new();
        IPEndPoint ep = new(IPAddress.Loopback, 8080);

        using WebSocketConnection connection1 = new(socket1, _extractor, ep);
        using WebSocketConnection connection2 = new(socket2, _extractor, ep);

        hub.RegisterConnection(connection1);
        hub.RegisterConnection(connection2);

        hub.ListConnections(connection1.NetworkEndpoint)
           .Should()
           .Contain(connection1)
           .And.Contain(connection2);

        hub.UnregisterConnection(connection1);

        hub.ListConnections(connection1.NetworkEndpoint)
           .Should()
           .NotContain(connection1)
           .And.Contain(connection2);
    }

    [Fact]
    public void UnregisterWebSocketConnection_WhenCalledTwice_IsIdempotent()
    {
        using ConnectionHub hub = new();
        using TestStubWebSocket socket = new();
        using WebSocketConnection connection = new(socket, _extractor, new IPEndPoint(IPAddress.Loopback, 8080));

        int raisedCount = 0;
        hub.ConnectionUnregistered += _ => raisedCount++;

        hub.RegisterConnection(connection);
        hub.UnregisterConnection(connection);
        hub.UnregisterConnection(connection);

        raisedCount.Should().Be(1);
        hub.Count.Should().Be(0);
    }
}
