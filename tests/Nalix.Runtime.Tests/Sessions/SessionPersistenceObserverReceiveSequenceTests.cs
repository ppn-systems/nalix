// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Sequencing;
using Nalix.Network.Connections;
using Nalix.Runtime.Sessions;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Sessions;

/// <summary>
/// Covers the close path where the receive snapshot is taken at <c>ConnectionUnregistered</c>.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class SessionPersistenceObserverReceiveSequenceTests
{
    [Fact]
    public void Observe_WhenFrameAcceptedAfterSnapshotBeforeDispose_SnapshotMissesThatFrame()
    {
        Bytes32 secret = new(new byte[Bytes32.Size] { 0x5A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 });
        SequenceCounter receive = new();
        receive.UpdateTo(10);
        using FakeConnection connection = new(new FakeTransport(new SequenceCounter(), receive)) { Secret = secret };
        ConnectionRuntimeStateAccessor.SetHandshakeEstablished(connection, true);

        RecordingSessionService service = new();
        using ConnectionHub hub = new();
        hub.RegisterConnection(connection);
        using SessionPersistenceObserver observer = new(hub, service);

        // Subscribed after the observer, so it runs after the snapshot was taken and before Dispose.
        // It stands in for a frame that the old connection's receive path accepts during the close.
        hub.ConnectionUnregistered += _ => receive.UpdateTo(11);

        hub.UnregisterConnection(connection);

        service.ReceiveAtSave.Should().Be(10u, "the snapshot is read when ConnectionUnregistered fires");
        receive.Current().Should().Be(11u, "the old connection accepted seq 11 after the snapshot");
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public uint ReceiveAtSave { get; private set; }

        public ValueTask SaveSessionAsync(IConnection connection, CancellationToken cancellationToken = default)
        {
            // Read synchronously, the same way the persistence path does before any await.
            this.ReceiveAtSave = connection.TCP.ReceiveSequence.Current();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SessionScope> ConsumeAsync(ulong sessionToken, Func<SessionEntry, bool>? predicate = null, CancellationToken cancellationToken = default)
            => new(new SessionScope(null));

        public void Dispose() { }
    }
}
