// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// <see cref="SocketConnection"/> instances are pooled. A receive loop that is still pending when its instance is reset and
/// handed to another connection must not report that other connection as closed.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class SocketConnectionRecycleTests
{
    [Fact]
    public async Task ReceiveLoop_WhenItEndsAfterTheInstanceWasRecycled_DoesNotCloseTheNewOwner()
    {
        using SocketPair stalePair = await SocketPair.CreateAsync();
        using SocketPair oldOwnerPair = await SocketPair.CreateAsync();
        using SocketPair newOwnerPair = await SocketPair.CreateAsync();
        using SocketPair recycledPair = await SocketPair.CreateAsync();

        using Connection oldOwner = new(oldOwnerPair.Server, new StubOpCodeExtractor());
        using Connection newOwner = new(newOwnerPair.Server, new StubOpCodeExtractor());

        TaskCompletionSource newOwnerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        newOwner.ConnectionClosed += (_, _) => newOwnerClosed.TrySetResult();

        SocketConnection pooled = new();
        pooled.Initialize(stalePair.Server, oldOwner);
        pooled.BeginReceive();

        // The pool resets the instance and gives it to another connection while the first receive loop is still waiting.
        pooled.ResetForPool();
        pooled.Initialize(recycledPair.Server, newOwner);

        // The peer of the first connection goes away, so the old loop now unwinds.
        stalePair.Client.Close();

        Task winner = await Task.WhenAny(newOwnerClosed.Task, Task.Delay(1000));

        winner.Should().NotBeSameAs(newOwnerClosed.Task, "a stale receive loop must not report the new owner of a recycled instance as closed");
    }

    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private sealed class SocketPair : IDisposable
    {
        private readonly Socket _listener;

        private SocketPair(Socket listener, Socket client, Socket server)
        {
            _listener = listener;
            this.Client = client;
            this.Server = server;
        }

        public Socket Client { get; }

        public Socket Server { get; }

        public static async Task<SocketPair> CreateAsync()
        {
            Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            Task<Socket> accept = listener.AcceptAsync();
            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);

            return new SocketPair(listener, client, await accept);
        }

        public void Dispose()
        {
            try { this.Client.Dispose(); } catch (ObjectDisposedException) { }
            try { this.Server.Dispose(); } catch (ObjectDisposedException) { }
            try { _listener.Dispose(); } catch (ObjectDisposedException) { }
        }
    }
}
