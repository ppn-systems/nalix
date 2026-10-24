// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Framework.Injection;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Time;
using Xunit;

namespace Nalix.Network.Tests.Connections;

[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketConnectionTimeoutTests : IDisposable
{
    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private readonly StubOpCodeExtractor _extractor = new();

    public void Dispose()
    {
        InstanceManager.Instance.Clear(dispose: false);
    }

    [Fact]
    public void UpdateIdleTimeout_WhenCalled_ReregistersInTimingWheel()
    {
        TimingWheel wheel = InstanceManager.Instance.GetOrCreateInstance<TimingWheel>();
        using StubWebSocket socket = new();
        using WebSocketConnection connection = new(socket, _extractor, new IPEndPoint(IPAddress.Loopback, 8080));

        wheel.Register(connection);
        connection.IsRegisteredInWheel.Should().BeTrue();

        var originalTask = ((TimingWheel.ITimeoutTrackedConnection)connection).TimeoutTask;
        originalTask.Should().NotBeNull();

        connection.UpdateIdleTimeout(5000);

        connection.IsRegisteredInWheel.Should().BeTrue();
        connection.IdleTimeoutMs.Should().Be(5000);
        var updatedTask = ((TimingWheel.ITimeoutTrackedConnection)connection).TimeoutTask;
        updatedTask.Should().NotBeNull();

        wheel.Unregister(connection);
        connection.IsRegisteredInWheel.Should().BeFalse();
    }

    [Fact]
    public void UpdateIdleTimeout_WithRapidRepeatedUpdates_DoesNotLeak()
    {
        TimingWheel wheel = InstanceManager.Instance.GetOrCreateInstance<TimingWheel>();
        using StubWebSocket socket = new();
        using WebSocketConnection connection = new(socket, _extractor, new IPEndPoint(IPAddress.Loopback, 8080));

        wheel.Register(connection);

        for (int i = 1; i <= 100; i++)
        {
            connection.UpdateIdleTimeout(1000 + i * 10);
        }

        connection.IsRegisteredInWheel.Should().BeTrue();
        connection.IdleTimeoutMs.Should().Be(2000);

        wheel.Unregister(connection);
        connection.IsRegisteredInWheel.Should().BeFalse();
        ((TimingWheel.ITimeoutTrackedConnection)connection).TimeoutTask.Should().BeNull();
    }

    [Fact]
    public void TimingWheelTick_WhenExcludedFromTimeout_UnregistersConnection()
    {
        TimingWheel wheel = InstanceManager.Instance.GetOrCreateInstance<TimingWheel>();
        using StubWebSocket socket = new();
        using WebSocketConnection connection = new(socket, _extractor, new IPEndPoint(IPAddress.Loopback, 8080));

        wheel.Register(connection);
        connection.IsRegisteredInWheel.Should().BeTrue();

        connection.ExcludeFromIdleTimeout = true;
        // LastPingTime set to a time far in the past to trigger timeout logic
        connection.LastPingTime = 0;

        // Force wheel processing
        wheel.Unregister(connection);

        connection.IsRegisteredInWheel.Should().BeFalse();
        ((TimingWheel.ITimeoutTrackedConnection)connection).TimeoutTask.Should().BeNull();
        connection.IsDisposed.Should().BeFalse("Excluded connection must not be disposed");
    }
}
