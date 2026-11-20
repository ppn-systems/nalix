// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
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

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// Regression: <see cref="Connection"/>-style owners expose a pooled <c>WebSocketTransport</c> through
/// <c>TCP</c>. A caller that kept that reference after the owner closed could send through it once the instance
/// was re-rented to another WebSocket connection, writing into that connection's socket.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketTransportStaleHolderTests
{
    private static readonly byte[] Payload = [0xAA, 0xBB, 0xCC, 0xDD];

    [Fact]
    public async Task SendAsync_OnStaleWebSocketHolderAfterOwnerClosed_DoesNotReachNewOwnerSocket()
    {
        EnsureRegistered();

        using RecordingWebSocket oldSocket = new();
        using RecordingWebSocket newSocket = new();

        WebSocketConnection oldOwner = new(oldSocket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        IConnection.ITransport stale = oldOwner.Transport;
        oldOwner.Dispose();

        // A new WebSocket session takes over the pooled transport instance.
        using WebSocketConnection newOwner = new(newSocket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));

        await stale.SendAsync(Payload);

        newSocket.Sent.Should().BeEmpty(
            "bytes sent through a stale WebSocket handle must not reach the socket of the connection that now owns the recycled transport");
    }

    [Fact]
    public async Task SendAsync_OnStaleWebSocketHolderAfterOwnerClosed_DoesNotReachOldSocket()
    {
        EnsureRegistered();

        using RecordingWebSocket oldSocket = new();
        WebSocketConnection oldOwner = new(oldSocket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        IConnection.ITransport stale = oldOwner.Transport;
        oldOwner.Dispose();

        Exception? error = await Record.ExceptionAsync(() => stale.SendAsync(Payload).AsTask());

        if (error is not null)
        {
            error.Should().BeOfType<ObjectDisposedException>("a closed WebSocket connection's transport may refuse to send, but must not fail with an unrelated error");
        }

        oldSocket.Sent.Should().BeEmpty("a closed WebSocket connection must not send anything");
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
    /// A socket that stays open, never receives, and records every message written to it.
    /// </summary>
    private sealed class RecordingWebSocket : WebSocket
    {
        private readonly List<byte[]> _sent = [];
        private int _state = (int)WebSocketState.Open;

        public List<byte[]> Sent
        {
            get { lock (_sent) { return [.. _sent]; } }
        }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);

        public override string? SubProtocol => null;

        public override void Abort() => Volatile.Write(ref _state, (int)WebSocketState.Aborted);

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => new TaskCompletionSource<WebSocketReceiveResult>().Task.WaitAsync(cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_sent)
            {
                _sent.Add(buffer.AsSpan().ToArray());
            }

            return Task.CompletedTask;
        }
    }
}
