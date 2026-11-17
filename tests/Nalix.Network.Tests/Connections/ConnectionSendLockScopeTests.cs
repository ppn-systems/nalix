// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// The send-lock scope of a connection is the connection itself. Disposing an acquired scope twice must not release the
/// lock twice, because the second release would free a lock that another sender may have taken in between.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class ConnectionSendLockScopeTests : IDisposable
{
    private readonly List<IDisposable> _resources = [];

    public void Dispose()
    {
        foreach (IDisposable resource in _resources)
        {
            try { resource.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenCalledTwice_ReleasesTheLockOnce()
    {
        Connection connection = CreateConnection();
        IAsyncDisposable scope = await connection.TCP.AcquireSendLockAsync();

        await scope.DisposeAsync();
        await scope.DisposeAsync();

        ValueTask<IAsyncDisposable> next = connection.TCP.AcquireSendLockAsync();
        next.IsCompletedSuccessfully.Should().BeTrue("the first release made the lock available");

        ValueTask<IAsyncDisposable> blocked = connection.TCP.AcquireSendLockAsync();
        blocked.IsCompleted.Should().BeFalse("a second release must not have freed a lock that the next holder still owns");

        await (await next.ConfigureAwait(false)).DisposeAsync();
        await blocked;
    }

    [Theory]
    [InlineData(typeof(Connection))]
    [InlineData(typeof(WebSocketConnection))]
    public void Connection_IsNotIAsyncDisposable(Type connectionType)
    {
        // Regression: when the connection itself was the send-lock scope, `await using` (and any disposer that prefers
        // DisposeAsync, such as PacketScope) only released the lock, so the connection, its socket and its pooled state leaked.
        connectionType.Should().NotBeAssignableTo<IAsyncDisposable>(
            "DisposeAsync on a connection would release the send lock instead of closing the connection");
    }

    [Fact]
    public async Task AcquireSendLockAsync_ReturnsAScopeThatIsNotTheConnection()
    {
        Connection connection = CreateConnection();

        IAsyncDisposable scope = await connection.TCP.AcquireSendLockAsync();
        await scope.DisposeAsync();

        scope.Should().NotBeSameAs(connection, "disposing the send-lock scope must never be confused with disposing the connection");
        connection.IsDisposed.Should().BeFalse();
    }

    private Connection CreateConnection()
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

        Connection connection = new(server, new DummyOpCodeExtractor());
        _resources.Add(connection);
        return connection;
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
