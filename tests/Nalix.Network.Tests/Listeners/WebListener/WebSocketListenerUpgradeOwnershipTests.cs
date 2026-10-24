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
