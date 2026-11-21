// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal.WebSockets;
using Nalix.Network.Listeners.Web;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners.WebListener;

/// <summary>
/// Drives the handshake-timeout sweep and the read-completion path of <see cref="WebSocketListenerBase"/>
/// in a fixed interleaving to verify that exactly one side owns a pending upgrade context.
/// </summary>
[Collection(TestCollections.AsyncCallback)]
public sealed class WebSocketListenerUpgradeOwnershipTests
{
    private sealed class StubWebSocketListener : WebSocketListenerBase
    {
        public StubWebSocketListener(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
            : base(12348, "/ws", protocol, hub, guard) { }
    }

    [Fact]
    public async Task TrackLiveConnection_AfterDeactivateWentOverTheLiveConnections_ClosesWithEndpointUnavailable()
    {
        // The 101 response completes the client's connect before the server tracks the connection, so Deactivate
        // can run in between. A connection tracked after that must still get the shutdown close status (1001),
        // not be left open and closed later with NormalClosure.
        using StubWebSocketListener listener = new(Substitute.For<IProtocol>(), Substitute.For<IConnectionHub>(), Substitute.For<IConnectionGuard>());
        listener.Deactivate();

        RecordingWebSocket webSocket = new();
        using Nalix.Network.Connections.WebSocketConnection connection = new(webSocket, Substitute.For<Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor>(), new IPEndPoint(IPAddress.Loopback, 0));

        Assert.False(listener.TrackLiveConnection(connection));
        Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, await webSocket.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TrackLiveConnection_WhileActive_KeepsTheConnectionOpen()
    {
        using StubWebSocketListener listener = new(Substitute.For<IProtocol>(), Substitute.For<IConnectionHub>(), Substitute.For<IConnectionGuard>());

        RecordingWebSocket webSocket = new();
        using Nalix.Network.Connections.WebSocketConnection connection = new(webSocket, Substitute.For<Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor>(), new IPEndPoint(IPAddress.Loopback, 0));

        Assert.True(listener.TrackLiveConnection(connection));
        Assert.False(webSocket.Closed.Task.IsCompleted);
    }

    private sealed class RecordingWebSocket : System.Net.WebSockets.WebSocket
    {
        private System.Net.WebSockets.WebSocketState _state = System.Net.WebSockets.WebSocketState.Open;

        public TaskCompletionSource<System.Net.WebSockets.WebSocketCloseStatus> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override System.Net.WebSockets.WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = System.Net.WebSockets.WebSocketState.Aborted;

        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => this.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = System.Net.WebSockets.WebSocketState.CloseSent;
            _ = this.Closed.TrySetResult(closeStatus);
            return Task.CompletedTask;
        }

        public override void Dispose() { }

        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.WebSockets.WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true));

        public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public void OnWebSocketReadCompleted_AfterSweepDetached_DoesNotTouchContextAndSweepClosesWholeChain()
    {
        IConnectionGuard guard = Substitute.For<IConnectionGuard>();
        using StubWebSocketListener listener = new(Substitute.For<IProtocol>(), Substitute.For<IConnectionHub>(), guard);

        IPEndPoint endpointA = new(IPAddress.Parse("10.0.0.1"), 1001);
        IPEndPoint endpointB = new(IPAddress.Parse("10.0.0.2"), 1002);
        using Socket socketA = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using Socket socketB = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        WebSocketUpgradeContext a = NewContext(socketA, endpointA);
        WebSocketUpgradeContext b = NewContext(socketB, endpointB);
        listener.EnqueueWsUpgradeContext(a);
        listener.EnqueueWsUpgradeContext(b);

        // Sweep step 1: detach both timed-out contexts into the local chain (b -> a).
        WebSocketUpgradeContext? chain = listener.DETACH_TIMED_OUT_WS_UPGRADES(long.MaxValue);
        Assert.Same(b, chain);

        // Read completion for b races in after the sweep detached it.
        using SocketAsyncEventArgs args = new() { UserToken = b, SocketError = SocketError.ConnectionReset };
        listener.OnWebSocketReadCompleted(socketB, args);

        // The completion path must not have released, closed or recycled b.
        Assert.Same(socketB, b.Socket);
        Assert.Same(a, b.Next);
        Assert.False(socketB.SafeHandle.IsClosed);
        guard.DidNotReceive().Release(Arg.Any<IPEndPoint>());
        Assert.Null(args.UserToken);

        // Sweep step 2: close outside the lock. Every context is closed and released exactly once.
        listener.CloseChainOutsideLock(chain);

        Assert.True(socketA.SafeHandle.IsClosed);
        Assert.True(socketB.SafeHandle.IsClosed);
        guard.Received(1).Release(endpointA);
        guard.Received(1).Release(endpointB);
    }

    [Fact]
    public void OnWebSocketReadCompleted_AfterCleanup_DoesNotTouchContext()
    {
        IConnectionGuard guard = Substitute.For<IConnectionGuard>();
        using StubWebSocketListener listener = new(Substitute.For<IProtocol>(), Substitute.For<IConnectionHub>(), guard);

        IPEndPoint endpoint = new(IPAddress.Parse("10.0.0.3"), 1003);
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        WebSocketUpgradeContext state = NewContext(socket, endpoint);
        listener.EnqueueWsUpgradeContext(state);

        listener.CLEANUP_WS_UPGRADES();

        using SocketAsyncEventArgs args = new() { UserToken = state, SocketError = SocketError.OperationAborted };
        listener.OnWebSocketReadCompleted(socket, args);

        Assert.Same(socket, state.Socket);
        Assert.Same(endpoint, state.RealEndPoint);
        Assert.True(state.RemovedFromList);
        Assert.True(socket.SafeHandle.IsClosed);
        guard.Received(1).Release(endpoint);
    }

    private static WebSocketUpgradeContext NewContext(Socket socket, IPEndPoint endpoint) => new()
    {
        Socket = socket,
        RealEndPoint = endpoint,
        HandshakeStartTimeTicks = 0,
    };
}
