// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Network.Connections;
using Nalix.Runtime.Sessions;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Sessions;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class SessionPersistenceObserverTests
{
    [Fact]
    public async Task Observe_WhenConnectionDisposedImmediatelyAfterEvent_PreservesSnapshotForSave()
    {
        byte[] secretBytes = new byte[32];
        secretBytes[0] = 0x5A;
        Bytes32 expectedSecret = new(secretBytes);
        AttributeKey testAttrKey = new(0x7777UL);

        using FakeConnection connection = new()
        {
            Secret = expectedSecret,
            Level = PermissionLevel.USER,
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            UserId = "user-42"
        };
        ConnectionRuntimeStateAccessor.SetHandshakeEstablished(connection, true);
        connection.Attributes[testAttrKey] = "preserved-value";

        TaskCompletionSource<IConnectionSnapshotResult> saveCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AsyncInspectingSessionService sessionService = new(saveCompleted);
        using ConnectionHub hub = new();
        hub.RegisterConnection(connection);

        Type? observerType = typeof(SessionService).Assembly.GetType("Nalix.Runtime.Sessions.SessionPersistenceObserver");
        observerType.Should().NotBeNull();

        using IDisposable observer = (IDisposable)Activator.CreateInstance(
            observerType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [hub, sessionService],
            culture: null)!;

        // ConnectionHub.UnregisterConnection fires ConnectionUnregistered, then immediately Disposes the connection synchronously
        hub.UnregisterConnection(connection);

        IConnectionSnapshotResult captured = await saveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        captured.WasDisposedDuringSave.Should().BeFalse("SaveSessionAsync must receive a non-disposed snapshot even after the live connection is disposed");
        captured.ObservedSecret.Should().Be(expectedSecret, "SaveSessionAsync must observe the connection's Secret before Dispose zeroes it");
        captured.ObservedAttribute.Should().Be("preserved-value", "SaveSessionAsync must observe the connection's Attributes before Dispose returns them to the pool");
    }

    private interface IConnectionSnapshotResult
    {
        bool WasDisposedDuringSave { get; }

        Bytes32 ObservedSecret { get; }

        object? ObservedAttribute { get; }
    }

    private sealed record ConnectionSnapshotResult(
        bool WasDisposedDuringSave,
        Bytes32 ObservedSecret,
        object? ObservedAttribute) : IConnectionSnapshotResult;

    private sealed class AsyncInspectingSessionService(TaskCompletionSource<IConnectionSnapshotResult> tcs) : ISessionService
    {
        private static readonly AttributeKey s_testKey = new(0x7777UL);

        public async ValueTask SaveSessionAsync(IConnection connection, CancellationToken cancellationToken = default)
        {
            await Task.Delay(30, cancellationToken);
            bool wasDisposed = connection.IsDisposed;
            Bytes32 secret = connection.Secret;
            _ = connection.Attributes.TryGetValue(s_testKey, out object? attr);
            tcs.TrySetResult(new ConnectionSnapshotResult(wasDisposed, secret, attr));
        }

        public ValueTask<SessionScope> ConsumeAsync(ulong sessionToken, Func<SessionEntry, bool>? predicate = null, CancellationToken cancellationToken = default)
            => new(new SessionScope(null));

        public void Dispose() { }
    }
}
