// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Xunit;

namespace Nalix.Network.Tests.Connections;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketConnectionSendTests : IDisposable
{
    private static readonly byte[] Payload = [1, 2, 3, 4];

    public void Dispose()
    {
        WebSocketConnection.s_postProcessHook = null;
    }

    [Fact]
    public async Task SendAsync_WhenPostSendNotificationRuns_SendLockIsAlreadyReleased()
    {
        EnsureRegistered();

        using ImmediateWebSocket socket = new();
        using WebSocketConnection connection = new(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));

        int lockCountDuringNotification = -1;
        WebSocketConnection.s_postProcessHook = c => lockCountDuringNotification = c.SendLock.CurrentCount;

        await connection.Transport.SendAsync(Payload);

        lockCountDuringNotification.Should().Be(1,
            "the post-send notification must run after the write lock is released, so a handler never runs under the lock");
    }

    [Fact]
    public async Task SendAsync_WhenConnectionClosedWhileWriting_IsANoOp()
    {
        EnsureRegistered();

        using PendingWebSocket socket = new();
        WebSocketConnection connection = new(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        Task pending = connection.Transport.SendAsync(Payload).AsTask();

        connection.Dispose();

        // The socket fails the write when it is aborted by the close. A send that was still in flight must complete quietly,
        // as a send after close does, instead of surfacing the socket's error.
        Exception? error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));

        error.Should().BeNull("a write that fails because the connection was closed must be a no-op, not an error");
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

    /// <summary>A socket whose writes complete immediately.</summary>
    private sealed class ImmediateWebSocket : WebSocket
    {
        private int _state = (int)WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override string? SubProtocol => null;
        public override void Abort() => Volatile.Write(ref _state, (int)WebSocketState.Aborted);
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => new TaskCompletionSource<WebSocketReceiveResult>().Task.WaitAsync(cancellationToken);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>A socket whose write stays pending until the socket is aborted, then fails the way an aborted socket does.</summary>
    private sealed class PendingWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override string? SubProtocol => null;

        public override void Abort()
        {
            Volatile.Write(ref _state, (int)WebSocketState.Aborted);
            _ = _write.TrySetException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => new TaskCompletionSource<WebSocketReceiveResult>().Task.WaitAsync(cancellationToken);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => _write.Task;
    }
}
