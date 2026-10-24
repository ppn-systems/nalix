// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// <see cref="WebSocketTransport"/> instances are pooled. A receive loop that is still unwinding when its transport has
/// already been disposed and handed to another connection must not act on that other connection.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketTransportRecycleTests
{
    [Fact]
    public async Task ReceiveLoop_WhenItEndsAfterTheTransportWasRecycled_DoesNotDisconnectTheNewOwner()
    {
        EnsureRegistered();

        using ControllableWebSocket oldSocket = new();
        using ControllableWebSocket newSocket = new();
        using WebSocketConnection oldOwner = new(oldSocket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        using WebSocketConnection newOwner = new(newSocket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));

        WebSocketTransport transport = new();
        transport.Initialize(oldOwner, oldSocket);
        transport.BeginReceive();
        await oldSocket.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Same sequence as WebSocketConnection.Dispose followed by the pool giving the instance to another connection.
        transport.Dispose();
        transport.ResetForPool();
        transport.Initialize(newOwner, newSocket);

        // Only now does the old receive loop observe its aborted socket and unwind.
        oldSocket.FailPendingReceive();
        await Task.Delay(500);

        newOwner.IsDisposed.Should().BeFalse("a stale receive loop must not disconnect the connection that now uses the pooled transport");
        newSocket.State.Should().Be(WebSocketState.Open);
    }

    private static void EnsureRegistered()
    {
        InstanceManager.Instance.Register<ILogger>(NullLogger.Instance);
        _ = ConfigurationManager.Instance.Get<Nalix.Framework.Options.ObjectPoolOptions>();
        _ = InstanceManager.Instance.GetOrCreateInstance<BufferPoolManager>();
        InstanceManager.Instance.Register<Nalix.Abstractions.IObjectPoolManager>(ObjectPoolManager.Shared);
        InstanceManager.Instance.Register<ObjectPoolManager>(ObjectPoolManager.Shared);
    }

    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    /// <summary>
    /// A socket whose pending receive completes only when the test says so, and which stays pending after
    /// <see cref="Abort"/> (a real socket finishes the receive asynchronously, possibly much later).
    /// </summary>
    private sealed class ControllableWebSocket : WebSocket
    {
        private readonly TaskCompletionSource<WebSocketReceiveResult> _pendingReceive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)WebSocketState.Open;

        public TaskCompletionSource ReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);

        public override string? SubProtocol => null;

        public void FailPendingReceive()
            => _ = _pendingReceive.TrySetException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));

        public override void Abort() => Volatile.Write(ref _state, (int)WebSocketState.Aborted);

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            _ = this.ReceiveStarted.TrySetResult();
            return _pendingReceive.Task;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
