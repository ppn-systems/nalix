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
using Nalix.Abstractions.Networking;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// A peer that sends an empty WebSocket message (zero-length payload) must not end the receive loop:
/// the message is simply ignored and the connection stays open.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketTransportEmptyMessageTests
{
    [Fact]
    public async Task ReceiveLoop_WhenPeerSendsEmptyMessage_KeepsReceiving()
    {
        EnsureRegistered();

        using EmptyThenPendingWebSocket socket = new();
        using WebSocketConnection owner = new(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));

        IConnection.ITransport transport = owner.Transport;
        transport.BeginReceive();

        await socket.SecondReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        owner.IsDisposed.Should().BeFalse("an empty message must not disconnect the client");
        socket.State.Should().Be(WebSocketState.Open);

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

    /// <summary>Returns one empty binary message, then a receive that never completes.</summary>
    private sealed class EmptyThenPendingWebSocket : WebSocket
    {
        private readonly TaskCompletionSource<WebSocketReceiveResult> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _receives;
        private int _state = (int)WebSocketState.Open;

        public TaskCompletionSource SecondReceiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);

        public override string? SubProtocol => null;

        public override void Abort() => Volatile.Write(ref _state, (int)WebSocketState.Aborted);

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => this.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receives) == 1)
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Binary, endOfMessage: true));
            }

            _ = this.SecondReceiveStarted.TrySetResult();
            return _pending.Task;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
