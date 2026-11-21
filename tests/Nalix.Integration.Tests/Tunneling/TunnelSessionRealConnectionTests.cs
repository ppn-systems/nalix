// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Tunneling;
using Xunit;

namespace Nalix.Integration.Tests.Tunneling;

/// <summary>
/// A tunnel unwraps both connections into raw sockets. These tests use real <see cref="Connection"/> objects, not fakes, so
/// the unwrap path that the tunneling extension depends on is exercised end to end.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class TunnelSessionRealConnectionTests : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly List<IDisposable> _resources = [];

    public void Dispose()
    {
        foreach (IDisposable resource in _resources)
        {
            try { resource.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    [Fact]
    public async Task StartTunnel_WithRealConnections_CarriesBytesInBothDirections()
    {
        (Socket consumerServer, Socket consumerPeer) = CreatePair();
        (Socket providerServer, Socket providerPeer) = CreatePair();

        Connection consumer = new(consumerServer, new DummyOpCodeExtractor());
        Connection provider = new(providerServer, new DummyOpCodeExtractor());
        _resources.Add(consumer);
        _resources.Add(provider);

        await using TunnelSession session = new(consumer, new TunnelOptions());
        session.StartTunnel(provider);

        byte[] toProvider = [1, 2, 3, 4, 5];
        byte[] toConsumer = [9, 8, 7, 6];

        _ = await consumerPeer.SendAsync(toProvider, SocketFlags.None);
        byte[] receivedByProvider = await ReadExactlyAsync(providerPeer, toProvider.Length);

        _ = await providerPeer.SendAsync(toConsumer, SocketFlags.None);
        byte[] receivedByConsumer = await ReadExactlyAsync(consumerPeer, toConsumer.Length);

        Assert.Equal(toProvider, receivedByProvider);
        Assert.Equal(toConsumer, receivedByConsumer);
    }

    private static async Task<byte[]> ReadExactlyAsync(Socket socket, int length)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None).AsTask().WaitAsync(ReadTimeout);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return buffer;
    }

    private (Socket Server, Socket Client) CreatePair()
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _resources.Add(listener);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _resources.Add(client);
        client.Connect((IPEndPoint)listener.LocalEndPoint!);

        Socket server = listener.Accept();
        _resources.Add(server);
        return (server, client);
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
