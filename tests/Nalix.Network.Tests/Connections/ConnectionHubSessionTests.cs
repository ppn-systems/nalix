// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using NSubstitute;
using Nalix.Abstractions.Concurrency;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Runtime.Sessions;
using Xunit;

using Nalix.Runtime.Extensions;

namespace Nalix.Network.Tests.Connections;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class ConnectionHubSessionTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(System.ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    [Fact]
    public async Task CreateSession_WithPopulatedConnection_CapturesConnectionState()
    {
        ISessionFactory factory = new SessionFactory();
        using ConnectionHub hub = new();
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

        connection.Secret = new Abstractions.Primitives.Bytes32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        connection.Algorithm = Abstractions.Security.CipherSuiteType.Chacha20Poly1305;
        connection.Level = Abstractions.Security.PermissionLevel.OWNER;
        connection.Attributes[AttributeKey.FromName("test")] = "value";

        SessionEntry session = factory.CreateSession(connection);

        _ = session.Should().NotBeNull();
        _ = session.Snapshot.Secret.Should().Be(connection.Secret);
        _ = session.Snapshot.Algorithm.Should().Be(connection.Algorithm);
        _ = session.Snapshot.Level.Should().Be(connection.Level);
        _ = session.Snapshot.Attributes.Should().ContainKey(AttributeKey.FromName("test")).WhoseValue.Should().Be("value");
        _ = session.ConnectionId.Should().Be(connection.ConnectionId);
    }

    [Fact]
    public async Task SessionResume_AfterOldConnectionUnregistered_RestoresState()
    {
        InMemorySessionStore store = new();
        SessionService service = new(store: store);
        using ConnectionHub hub = new();
        using SessionPersistenceObserver observer = new(hub, service);
        using ConnectedSocketScope scope1 = await ConnectedSocketScope.CreateAsync();
        using Connection connection1 = new(scope1.ServerSocket, s_testOpCodeExtractor);

        connection1.Secret = new Abstractions.Primitives.Bytes32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        connection1.Attributes[AttributeKey.FromName("test")] = "value";
        connection1.GetRuntimeState().HandshakeEstablished = true;
        hub.RegisterConnection(connection1);

        ISessionFactory factory = new SessionFactory();
        SessionEntry sessionInfo = factory.CreateSession(connection1);

        await store.StoreAsync(sessionInfo);

        // Resume should fail while connection1 is still in Hub
        using ConnectedSocketScope scope2 = await ConnectedSocketScope.CreateAsync();
        using Connection connection2 = new(scope2.ServerSocket, s_testOpCodeExtractor);

        // Simulator of TryResumeSession logic:
        // We use ConsumeAsync but since we want to check multiple times, we'll store it back or use the reference
        SessionScope sessionScope1 = await service.ConsumeAsync(sessionInfo.Snapshot.SessionToken);
        SessionEntry? s = sessionScope1.Value;
        _ = s.Should().NotBeNull();
        await store.StoreAsync(s!);

        // Security validation (hub.GetConnection)
        _ = hub.GetConnection(s!.ConnectionId).Should().NotBeNull();

        // Unregister connection1 (this will dispose it and wipe its secret)
        var expectedSecret = connection1.Secret;
        hub.UnregisterConnection(connection1);

        // Resume should now succeed
        using SessionScope sessionScope2 = await service.ConsumeAsync(sessionInfo.Snapshot.SessionToken);
        SessionEntry? ss = sessionScope2.Value;
        _ = ss.Should().NotBeNull();
        _ = hub.GetConnection(ss!.ConnectionId).Should().BeNull();

        // Manual state restoration
        ApplySession(connection2, ss);

        _ = connection2.Secret.Should().Be(expectedSecret);
        _ = connection2.Attributes[AttributeKey.FromName("test")].Should().Be("value");
        _ = connection2.GetRuntimeState().HandshakeEstablished.Should().Be(true);
    }

    [Fact]
    public async Task SessionResume_AfterStateModifications_RestoresModifiedState()
    {
        InMemorySessionStore store = new();
        SessionService service = new(store: store);
        using ConnectionHub hub = new();
        using SessionPersistenceObserver observer = new(hub, service);
        using ConnectedSocketScope scope1 = await ConnectedSocketScope.CreateAsync();
        using Connection connection1 = new(scope1.ServerSocket, s_testOpCodeExtractor);

        connection1.GetRuntimeState().HandshakeEstablished = true;
        hub.RegisterConnection(connection1);

        // 1. Create session (Initial state)
        ISessionFactory factory = new SessionFactory();
        SessionEntry sessionInfo = factory.CreateSession(connection1);
        await store.StoreAsync(sessionInfo);

        // 2. Modify state AFTER session creation
        connection1.Attributes[AttributeKey.FromName("user_id")] = 12345;
        connection1.Level = Abstractions.Security.PermissionLevel.SYSTEM_ADMINISTRATOR;

        // 3. Update session before unregistering (required now because Unregister doesn't auto-update)
        SyncSession(connection1, sessionInfo);
        await store.StoreAsync(sessionInfo);

        // 4. Unregister connection
        hub.UnregisterConnection(connection1);

        // 5. Resume session with new connection
        using ConnectedSocketScope scope2 = await ConnectedSocketScope.CreateAsync();
        using Connection connection2 = new(scope2.ServerSocket, s_testOpCodeExtractor);
        using SessionScope sessionScope = await service.ConsumeAsync(sessionInfo.Snapshot.SessionToken);
        SessionEntry? s = sessionScope.Value;
        _ = s.Should().NotBeNull();

        ApplySession(connection2, s!);

        // 6. Verify that modifications were persisted
        _ = connection2.Attributes.Should().ContainKey(AttributeKey.FromName("user_id")).WhoseValue.Should().Be(12345);
        _ = connection2.Level.Should().Be(Abstractions.Security.PermissionLevel.SYSTEM_ADMINISTRATOR);
    }

    [Fact]
    public async Task SessionSync_WhenAttributeModified_StoresUpdatedSnapshot()
    {
        InMemorySessionStore store = new();
        SessionService service = new(store: store);
        using ConnectionHub hub = new();
        using SessionPersistenceObserver observer = new(hub, service);
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

        hub.RegisterConnection(connection);
        ISessionFactory factory = new SessionFactory();
        SessionEntry sessionInfo = factory.CreateSession(connection);
        await store.StoreAsync(sessionInfo);

        // Modify state
        connection.Attributes[AttributeKey.FromName("custom")] = "data";

        // Manual sync
        SyncSession(connection, sessionInfo);
        await store.StoreAsync(sessionInfo);

        // Verify snapshot from store
        using SessionScope sessionScope = await service.ConsumeAsync(sessionInfo.Snapshot.SessionToken);
        SessionEntry? updated = sessionScope.Value;
        _ = updated.Should().NotBeNull();
        _ = updated!.Snapshot.Attributes.Should().ContainKey(AttributeKey.FromName("custom")).WhoseValue.Should().Be("data");
    }

    [Fact]
    public async Task StoreAsync_WhenTokenIsOverwritten_ReclaimsPreviousEntry()
    {
        InMemorySessionStore store = new();
        SessionService service = new(store: store);
        using ConnectionHub hub = new();
        using SessionPersistenceObserver observer = new(hub, service);
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

        connection.Secret = new Abstractions.Primitives.Bytes32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        connection.Attributes[AttributeKey.FromName("marker")] = "old";

        ISessionFactory factory = new SessionFactory();
        SessionEntry original = factory.CreateSession(connection);
        await store.StoreAsync(original);

        SessionSnapshot replacementSnapshot = new()
        {
            SessionToken = original.Snapshot.SessionToken,
            CreatedAtUnixMilliseconds = original.Snapshot.CreatedAtUnixMilliseconds,
            ExpiresAtUnixMilliseconds = original.Snapshot.ExpiresAtUnixMilliseconds,
            Secret = new Abstractions.Primitives.Bytes32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            Algorithm = original.Snapshot.Algorithm,
            Level = original.Snapshot.Level,
            Attributes = ObjectMap<AttributeKey, object>.Rent()
        };
        replacementSnapshot.Attributes![AttributeKey.FromName("marker")] = "new";

        SessionEntry replacement = new(replacementSnapshot, original.ConnectionId);
        await store.StoreAsync(replacement);

        _ = original.Snapshot.Secret.Should().Be(Abstractions.Primitives.Bytes32.Zero);
        _ = original.Snapshot.Attributes.Should().BeNull();

        using SessionScope sessionScope = await service.ConsumeAsync(original.Snapshot.SessionToken);
        SessionEntry? stored = sessionScope.Value;
        _ = stored.Should().BeSameAs(replacement);
    }

    [Fact]
    public async Task ObserverDispose_ThenUnregisterConnection_DoesNotPersistSession()
    {
        InMemorySessionStore store = new();
        SessionService service = new(store: store);
        using ConnectionHub hub = new();
        SessionPersistenceObserver observer = new(hub, service);
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);

        connection.GetRuntimeState().HandshakeEstablished = true;
        for (int i = 0; i < 20; i++)
        {
            connection.Attributes[AttributeKey.FromName($"key_{i}")] = i;
        }

        hub.RegisterConnection(connection);

        // Dispose the observer BEFORE unregistering — it must unsubscribe from the hub event.
        observer.Dispose();

        hub.UnregisterConnection(connection);

        // Give any (incorrectly still-subscribed) background persistence a moment to run.
        await Task.Delay(200);

        using SessionScope sessionScope = await service.ConsumeAsync(connection.ConnectionId);
        _ = sessionScope.Value.Should().BeNull(
            "a disposed SessionPersistenceObserver must have unsubscribed from ConnectionUnregistered and not persist the session");
    }

    private static void SyncSession(IConnection connection, SessionEntry session)
    {
        SessionSnapshot old = session.Snapshot;

        ObjectMap<AttributeKey, object> attrs = ObjectMap<AttributeKey, object>.Rent();
        foreach (KeyValuePair<AttributeKey, object> attr in connection.Attributes)
        {
            attrs[attr.Key] = attr.Value;
        }

        session.Snapshot = new SessionSnapshot
        {
            SessionToken = old.SessionToken,
            CreatedAtUnixMilliseconds = old.CreatedAtUnixMilliseconds,
            ExpiresAtUnixMilliseconds = old.ExpiresAtUnixMilliseconds,
            Secret = connection.Secret,
            Algorithm = connection.Algorithm,
            Level = connection.Level,
            Attributes = attrs
        };

        old.Return();
    }

    private static void ApplySession(IConnection connection, SessionEntry session)
    {
        SessionSnapshot snapshot = session.Snapshot;
        connection.Secret = snapshot.Secret;
        connection.Algorithm = snapshot.Algorithm;
        connection.Level = snapshot.Level;

        if (snapshot.Attributes != null)
        {
            foreach (KeyValuePair<AttributeKey, object> attr in snapshot.Attributes)
            {
                connection.Attributes[attr.Key] = attr.Value;
            }
        }

        connection.GetRuntimeState().HandshakeEstablished = true;
        connection.Attributes[AttributeKey.FromName("nalix.session.token")] = snapshot.SessionToken;
    }

    private sealed class ConnectedSocketScope : IDisposable
    {
        private ConnectedSocketScope(Socket listenerSocket, Socket clientSocket, Socket serverSocket)
        {
            this.ListenerSocket = listenerSocket;
            this.ClientSocket = clientSocket;
            this.ServerSocket = serverSocket;
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
            try
            {
                this.ClientSocket.Dispose();
            }
            catch (SocketException ex)
            {
                Trace.WriteLine($"Ignoring SocketException while disposing ClientSocket: {ex}");
            }
            catch (ObjectDisposedException ex)
            {
                Trace.WriteLine($"Ignoring ObjectDisposedException while disposing ClientSocket: {ex}");
            }

            try
            {
                this.ServerSocket.Dispose();
            }
            catch (SocketException ex)
            {
                Trace.WriteLine($"Ignoring SocketException while disposing ServerSocket: {ex}");
            }
            catch (ObjectDisposedException ex)
            {
                Trace.WriteLine($"Ignoring ObjectDisposedException while disposing ServerSocket: {ex}");
            }

            try
            {
                this.ListenerSocket.Dispose();
            }
            catch (SocketException ex)
            {
                Trace.WriteLine($"Ignoring SocketException while disposing ListenerSocket: {ex}");
            }
            catch (ObjectDisposedException ex)
            {
                Trace.WriteLine($"Ignoring ObjectDisposedException while disposing ListenerSocket: {ex}");
            }
        }
    }
}
