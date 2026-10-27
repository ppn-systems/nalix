// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal.WebSockets;
using Nalix.Network.Listeners.Web;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners.WebListener;

[Collection(TestCollections.AsyncCallback)]
public sealed class WebSocketListenerHttpResponseTests
{
    private delegate void SendStatic(WebSocketUpgradeContext state, SocketAsyncEventArgs args, ReadOnlySpan<byte> response);

    private sealed class StubWebSocketListener : WebSocketListenerBase
    {
        public StubWebSocketListener(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
            : base(12351, "/ws", protocol, hub, guard) { }

        public void SendStaticResponse(WebSocketUpgradeContext state, SocketAsyncEventArgs args, byte[] response) =>
            typeof(WebSocketListenerBase)
                .GetMethod("SEND_STATIC_RESPONSE", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate<SendStatic>(this)(state, args, response);
    }

    [Fact]
    public async Task SendStaticResponse_WhenPeerNeverReads_GivesUpInsteadOfBlockingForever()
    {
        using StubWebSocketListener listener = new(Substitute.For<IProtocol>(), Substitute.For<IConnectionHub>(), Substitute.For<IConnectionGuard>());

        using Socket acceptor = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        acceptor.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        acceptor.Listen(1);
        Task<Socket> acceptTask = Task.Run(() => acceptor.Accept());

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(acceptor.LocalEndPoint!);
        using Socket server = await acceptTask;

        WebSocketUpgradeContext state = new() { Socket = server };
        using SocketAsyncEventArgs args = new();

        // Far larger than the loopback send/receive buffers, and the client never reads.
        byte[] response = new byte[64 * 1024 * 1024];

        Task send = Task.Run(() => listener.SendStaticResponse(state, args, response));
        Task completed = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(10)));

        // Unblock the sender either way so the test never leaks a stuck thread.
        client.Close();
        await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(10)));

        send.IsFaulted.Should().BeFalse("the send helper itself must not fail");
        completed.Should().BeSameAs(send, "HTTP route responses must use a send timeout like the handshake response does");
    }
}
