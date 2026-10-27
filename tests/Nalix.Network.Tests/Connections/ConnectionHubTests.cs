// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Concurrency;
using Nalix.Hosting.Internal;
using Nalix.Network.Connections;
using Nalix.Runtime.Sessions;
using NSubstitute;

using Nalix.Runtime.Extensions;

namespace Nalix.Network.Tests.Connections;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class ConnectionHubTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(System.ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    [Fact]
    public async Task RegisterConnection_WithNewConnection_IncrementsCountAndAllowsLookup()
    {
        using ConnectionHub hub = new();
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

        hub.RegisterConnection(connection);

        hub.Count.Should().Be(1);
        hub.GetConnection(connection.ConnectionId).Should().BeSameAs(connection);
    }

    [Fact]
    public async Task UnregisterConnection_AfterRegister_DecrementsCountAndRaisesEvent()
    {
        using ConnectionHub hub = new();
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

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
    public async Task ListConnections_ByEndpoint_UsesAddressIndex()
    {
        using ConnectionHub hub = new();
        using ConnectedSocketScope scope1 = await ConnectedSocketScope.CreateAsync();
        using ConnectedSocketScope scope2 = await ConnectedSocketScope.CreateAsync();
        using Connection connection1 = new(scope1.ServerSocket, s_testOpCodeExtractor);
        using Connection connection2 = new(scope2.ServerSocket, s_testOpCodeExtractor);

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
    public async Task UnregisterConnection_WhenCalledTwice_RaisesEventOnlyOnce()
    {
        using ConnectionHub hub = new();
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

        int raisedCount = 0;
        hub.ConnectionUnregistered += _ => raisedCount++;

        hub.RegisterConnection(connection);
        hub.UnregisterConnection(connection);
        hub.UnregisterConnection(connection);

        raisedCount.Should().Be(1, "unregistering an already-unregistered connection must be a no-op, not re-raise the event");
        hub.Count.Should().Be(0);
    }

    [Fact]
    public async Task UnregisterConnection_WhenSessionStoreFails_ReclaimsSessionSnapshot()
    {
        var testPolicy = NSubstitute.Substitute.For<ISessionPersistencePolicy>();
        testPolicy.ShouldPersist(NSubstitute.Arg.Any<IConnection>()).Returns(true);

        using FailingSessionStore failingStore = new();
        using SessionService sessionService = new(store: failingStore, policy: testPolicy);
        using ConnectionHub hub = new();
        using SessionPersistenceObserver observer = new(hub, sessionService);
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

        connection.Secret = new Bytes32(RandomNumberGenerator.GetBytes(Bytes32.Size));
        connection.GetRuntimeState().HandshakeEstablished = true;
        connection.Attributes[AttributeKey.FromName("attr-1")] = 1;
        connection.Attributes[AttributeKey.FromName("attr-2")] = 2;
        connection.Attributes[AttributeKey.FromName("attr-3")] = 3;
        connection.Attributes[AttributeKey.FromName("attr-4")] = 4;
        connection.Attributes[AttributeKey.FromName("attr-5")] = 5;

        hub.RegisterConnection(connection);
        hub.UnregisterConnection(connection);

        SessionEntry attempted = await failingStore.WaitForStoreAttemptAsync(TimeSpan.FromSeconds(3));

        // Wait for background persistence to finish throwing and reclaiming
        bool reclaimed = false;
        for (int i = 0; i < 50; i++)
        {
            if (attempted.Snapshot.Secret == Bytes32.Zero)
            {
                reclaimed = true;
                break;
            }
            await Task.Delay(10);
        }

        reclaimed.Should().BeTrue("Session secret should be zeroed after store failure.");
        attempted.Snapshot.Attributes.Should().BeNull();
    }

    [Fact]
    public async Task RegisterConnection_WhenTransportCloseDispatchIsInFlight_DoesNotLeaveClosedConnectionRegistered()
    {
        using ConnectionHub hub = new();
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

        using ManualResetEventSlim handlerEntered = new(false);
        using ManualResetEventSlim releaseHandler = new(false);
        connection.ConnectionClosed += (_, _) =>
        {
            handlerEntered.Set();
            releaseHandler.Wait(TimeSpan.FromSeconds(10));
        };

        connection.OnTransportClosed();
        handlerEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        // The close snapshot of the handler list is already taken: the hub's own handler can no longer fire.
        hub.RegisterConnection(connection);
        releaseHandler.Set();

        SpinWait.SpinUntil(() => connection.IsDisposed, TimeSpan.FromSeconds(10)).Should().BeTrue();
        hub.Count.Should().Be(0, "a connection that finished closing must not stay registered forever");
    }

    private sealed class ConnectedSocketScope : IDisposable
    {
        private ConnectedSocketScope(Socket listenerSocket, Socket clientSocket, Socket serverSocket)
        {
            ListenerSocket = listenerSocket;
            ClientSocket = clientSocket;
            ServerSocket = serverSocket;
        }

        public Socket ListenerSocket { get; }

        public Socket ClientSocket { get; }

        public Socket ServerSocket { get; }

        public static async Task<ConnectedSocketScope> CreateAsync()
        {
            Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            Task<Socket> acceptTask = Task.Run(() => listener.Accept());

            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, port);

            Socket server = await acceptTask;
            return new ConnectedSocketScope(listener, client, server);
        }

        public void Dispose()
        {
            try { ClientSocket.Dispose(); } catch { }
            try { ServerSocket.Dispose(); } catch { }
            try { ListenerSocket.Dispose(); } catch { }
        }
    }

    private static ulong ComposeSnowflakeId(uint sequence)
        => ((ulong)(byte)SnowflakeType.Session << 56)
         | ((ulong)0x12345678u << 24)
         | ((ulong)(sequence & 0x3FFFu) << 10)
         | 1UL;

    private sealed class FailingSessionStore : ISessionStore, IDisposable
    {
        private readonly TaskCompletionSource<SessionEntry> _storeAttempt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionEntry> WaitForStoreAttemptAsync(TimeSpan timeout)
        {
            using CancellationTokenSource cts = new(timeout);
            Task completed = await Task.WhenAny(_storeAttempt.Task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
            if (completed != _storeAttempt.Task)
            {
                throw new TimeoutException("Session store was not invoked in time.");
            }

            return await _storeAttempt.Task;
        }

        public ValueTask StoreAsync(SessionEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _storeAttempt.TrySetResult(entry);
            throw new InvalidOperationException("Simulated session-store failure.");
        }

        public ValueTask<SessionScope> ConsumeAsync(ulong sessionToken, Func<SessionEntry, bool>? predicate = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SessionScope(null));
        }

        public void Dispose()
        {
        }
    }
}
