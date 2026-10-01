#if DEBUG
// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Xunit;

namespace Nalix.Runtime.Tests;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PacketContextScopePoolingTests
{
    [Fact]
    public async Task PacketContext_DefaultInitialize_UsesEmbeddedDefaultScope()
    {
        // Arrange
        PacketContext<TestPacket> context = new();
        FakeConnection connection = new();
        TestPacket packet = new();
        PacketMetadata metadata = CreateMetadata();

        try
        {
            // Act: Initialize without passing an explicit scope
            context.Initialize(packet, connection, metadata, reliable: true, encryptedOnWire: false);

            // Assert: Scope must be non-null and of type PacketScope
            context.Scope.Should().NotBeNull();
            context.Scope.Should().BeOfType<PacketScope>();

            // Register disposable tracker
            DisposableTracker tracker = new();
            context.Scope.RegisterForDisposal(tracker);

            // Act: Async dispose
            await context.DisposeAsync();

            // Assert: Disposable registered in the default scope must be disposed
            tracker.IsDisposedSync.Should().BeTrue();
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public void PacketContext_ResetForPool_ResetsEmbeddedDefaultScopeDirectly()
    {
        // Arrange
        PacketContext<TestPacket> context = new();
        FakeConnection connection = new();
        TestPacket packet = new();
        PacketMetadata metadata = CreateMetadata();

        try
        {
            // First cycle
            context.Initialize(packet, connection, metadata, reliable: true, encryptedOnWire: false);
            Nalix.Abstractions.Injection.IPacketScope firstScope = context.Scope;
            DisposableTracker tracker1 = new();
            context.Scope.RegisterForDisposal(tracker1);

            context.Dispose();
            tracker1.IsDisposedSync.Should().BeTrue();

            // Second cycle: Re-initialize the same context
            context.Initialize(packet, connection, metadata, reliable: true, encryptedOnWire: false);
            Nalix.Abstractions.Injection.IPacketScope secondScope = context.Scope;

            // Assert: Must reuse the same embedded instance
            secondScope.Should().BeSameAs(firstScope, "embedded _scope must be preserved across pool resets");

            DisposableTracker tracker2 = new();
            context.Scope.RegisterForDisposal(tracker2);

            context.Dispose();
            tracker2.IsDisposedSync.Should().BeTrue();
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task PacketContext_CustomPassedInScope_IsNotDefaultScope_AndDisposesCorrectly()
    {
        // Arrange
        PacketContext<TestPacket> context = new();
        FakeConnection connection = new();
        TestPacket packet = new();
        PacketMetadata metadata = CreateMetadata();
        PacketScope customScope = ObjectPoolManager.Shared.Get<PacketScope>();

        try
        {
            // Act: Initialize passing custom scope
            context.Initialize(
                packet, connection, metadata,
                reliable: true, encryptedOnWire: false,
                scope: customScope);

            // Assert: Scope must be the custom scope
            context.Scope.Should().BeSameAs(customScope);

            DisposableTracker tracker = new();
            context.Scope.RegisterForDisposal(tracker);

            await context.DisposeAsync();

            tracker.IsDisposedSync.Should().BeTrue();
        }
        finally
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task PacketContext_DefaultScope_PreservesLifoDisposalOrder()
    {
        // Arrange
        PacketContext<TestPacket> context = new();
        FakeConnection connection = new();
        TestPacket packet = new();
        PacketMetadata metadata = CreateMetadata();
        List<int> order = [];

        try
        {
            context.Initialize(packet, connection, metadata, reliable: true, encryptedOnWire: false);

            context.Scope.RegisterForDisposal(new OrderTracker(1, order));
            context.Scope.RegisterForDisposal(new OrderTracker(2, order));
            context.Scope.RegisterForDisposal(new OrderTracker(3, order));

            await context.DisposeAsync();

            order.Should().Equal([3, 2, 1], "scope disposables must be disposed in LIFO order");
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static PacketMetadata CreateMetadata() =>
        new(
            opCode: new PacketOpcodeAttribute(1),
            timeout: null,
            permission: null,
            encryption: null,
            rateLimit: null,
            transport: null);

    private sealed class DisposableTracker : IDisposable
    {
        public bool IsDisposedSync { get; private set; }
        public void Dispose() => IsDisposedSync = true;
    }

    private sealed class OrderTracker(int id, List<int> order) : IDisposable
    {
        public void Dispose() => order.Add(id);
    }

    private sealed class TestPacket : IPacket
    {
        public int Length => 0;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => [];
        public int Serialize(Span<byte> buffer) => 0;
    }

    private sealed class FakeTransport : IConnection.ITransport
    {
        public TransportFraming Framing => TransportFraming.UInt16LengthPrefixed;
        public ISequenceCounter SendSequence { get; } = new DummySequenceCounter();
        public ISequenceCounter ReceiveSequence { get; } = new DummySequenceCounter();
        public uint NextSendSequence() => SendSequence.Next();
        public uint NextReceiveSequence() => ReceiveSequence.Next();
        public uint CurrentSendSequence => SendSequence.Current();
        public uint CurrentReceiveSequence => ReceiveSequence.Current();
        public void Send(ReadOnlySpan<byte> message) { }
        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void BeginReceive(CancellationToken cancellationToken = default) { }
        public void UseFraming(TransportFraming framing) { }
    }

    private sealed class DummySequenceCounter : ISequenceCounter
    {
        private uint _value;
        public uint Next() => ++_value;
        public uint Current() => _value;
        public void Reset(uint newValue = 0) => _value = newValue;
        public bool IsValid(uint? receivedSeq, uint window = 0) => true;
        public void UpdateTo(uint receivedSeq) => _value = receivedSeq;
        public void ResumeFrom(uint lastKnownSeq, uint safetyGap = 1000) => _value = lastKnownSeq;
        public bool IsApproachingOverflow(uint margin = 1000000) => false;
    }

    private sealed class FakeConnection : IConnection
    {
        public FakeTransport FakeTcp { get; } = new();
        public bool IsDisposed { get; private set; }
        public bool IsUdpCreated => false;
        public ulong ConnectionId => 1;
        public string? UserId { get; set; }
        public long UpTime => 0;
        public long LastPingTime => 0;
        public bool ExcludeFromIdleTimeout { get; set; }
        public IOpCodeExtractor PacketClassifier => null!;
        public INetworkEndpoint NetworkEndpoint => null!;
        public IObjectMap<AttributeKey, object> Attributes { get; } = ObjectMap<AttributeKey, object>.Rent();
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public Bytes32 Secret { get; set; }
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }
        public IConnection.ITransport TCP => FakeTcp;
        public IConnection.ITransport? UDP => null;
#pragma warning disable CS0067
        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;
#pragma warning restore CS0067
        public void Disconnect(string? reason = null) { }
        public void Dispose() => IsDisposed = true;
        public int ErrorCount => 0;
        public void IncrementErrorCount() { }
        public int IdleTimeoutMs { get; set; } = 60000;
        public void UpdateIdleTimeout(int newTimeoutMs) => IdleTimeoutMs = newTimeoutMs;
    }
}
#endif
