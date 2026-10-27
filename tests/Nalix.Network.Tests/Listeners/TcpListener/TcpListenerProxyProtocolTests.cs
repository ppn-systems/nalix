// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal.Tcp;
using Nalix.Network.Listeners.Tcp;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners.TcpListener;

public sealed class TcpListenerProxyProtocolTests
{
    private sealed class ThrowingProxyTcpListener : TcpListenerBase
    {
        public ThrowingProxyTcpListener(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
            : base(12350, protocol, hub, guard)
        {
        }

        public void BeginProxyHeaderRead(Socket socket) =>
            typeof(TcpListenerBase)
                .GetMethod("BeginProxyHeaderRead", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(this, [socket]);

        internal override AcceptResult ProcessProxyAcceptedSocket(Socket socket, EndPoint? realEndPoint, int headerBytesConsumed, byte[]? receiveBuffer, int bytesReceived)
            => throw new InvalidOperationException("simulated initialization failure");
    }

    [Fact]
    public async Task OnProxyReadCompleted_WhenConnectionInitializationThrows_ClosesTheAcceptedSocket()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        guard.TryAccept(Arg.Any<IPEndPoint>()).Returns(true);
        using var listener = new ThrowingProxyTcpListener(protocol, hub, guard);

        using Socket acceptor = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        acceptor.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        acceptor.Listen(1);
        Task<Socket> acceptTask = Task.Run(() => acceptor.Accept());

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(acceptor.LocalEndPoint!);
        using Socket server = await acceptTask;

        listener.BeginProxyHeaderRead(server);
        _ = await client.SendAsync(Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.7 198.51.100.1 40000 443\r\n"), SocketFlags.None);

        // A closed server socket surfaces as a zero-byte read on the client; an abandoned one just times out.
        byte[] buffer = new byte[1];
        Task<int> read = client.ReceiveAsync(buffer, SocketFlags.None);
        Task completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().BeSameAs(read, "a failed initialization must close the accepted socket instead of leaking it");
    }
}
