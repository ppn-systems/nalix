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
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// A graceful close must read the peer's frames until its close frame arrives before the socket is released. Frames
/// left unread make the kernel answer with a TCP reset, and a client that is still sending then sees a broken pipe or
/// 1006 instead of the 1001 it was sent (kitos#340).
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketConnectionShutdownTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CloseAsync_WhenPeerHasFramesInFlight_ReadsUntilPeerCloseBeforeReleasingSocket()
    {
        EnsureRegistered();

        using InFlightWebSocket socket = new(framesInFlight: 5);
        using WebSocketConnection connection = new(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        connection.Transport.BeginReceive();

        await connection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Server shutting down").WaitAsync(Timeout);

        socket.SentCloseStatus.Should().Be(WebSocketCloseStatus.EndpointUnavailable);
        socket.UnreadFramesAtRelease.Should().Be(0,
            "frames the peer sent before its close frame must be read, or the kernel resets the connection");
        socket.PeerCloseRead.Should().BeTrue("the closing handshake ends when the peer's close frame is read");
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
    /// A peer that is sending when the server starts to close: once the server's close frame is out, the next reads
    /// return the data frames that were already in flight, then the peer's own close frame.
    /// </summary>
    private sealed class InFlightWebSocket(int framesInFlight) : WebSocket
    {
        private readonly TaskCompletionSource _closeSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _framesLeft = framesInFlight;
        private int _state = (int)WebSocketState.Open;
        private int _released;

        public WebSocketCloseStatus? SentCloseStatus { get; private set; }

        public int UnreadFramesAtRelease { get; private set; } = -1;

        public bool PeerCloseRead { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => this.SentCloseStatus;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override string? SubProtocol => null;

        public override void Abort()
        {
            this.Release();
            Volatile.Write(ref _state, (int)WebSocketState.Aborted);
        }

        public override void Dispose() => this.Release();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => this.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.SentCloseStatus = closeStatus;
            _ = Interlocked.CompareExchange(ref _state, (int)WebSocketState.CloseSent, (int)WebSocketState.Open);
            _ = _closeSent.TrySetResult();
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            // Nothing arrives before the close starts; the in-flight frames reach the server after it.
            await _closeSent.Task.WaitAsync(cancellationToken);

            if (Interlocked.Decrement(ref _framesLeft) >= 0)
            {
                buffer.AsSpan(0, 16).Clear();
                return new WebSocketReceiveResult(16, WebSocketMessageType.Binary, endOfMessage: true);
            }

            this.PeerCloseRead = true;
            _ = Interlocked.CompareExchange(ref _state, (int)WebSocketState.Closed, (int)WebSocketState.CloseSent);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.EndpointUnavailable, null);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                this.UnreadFramesAtRelease = Math.Max(0, Volatile.Read(ref _framesLeft));
            }
        }
    }
}
