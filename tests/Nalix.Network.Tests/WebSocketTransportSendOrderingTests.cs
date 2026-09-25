// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.Tests;

/// <summary>
/// Regression tests for the WebSocket sequence-reservation/wire-write race fixed via
/// <see cref="IConnection.ITransport.AcquireSendLockAsync"/>/<see cref="IConnection.ITransport.SendAsyncCore(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>.
/// Without holding the send lock across reservation + write, concurrent senders can reserve
/// sequence numbers in one order but land on the wire in the opposite order.
/// </summary>
public sealed class WebSocketTransportSendOrderingTests
{
    private sealed class StubOpCodeExtractor : Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    /// <summary>
    /// A WebSocket stub whose SendAsync artificially delays proportionally to the first byte of
    /// the payload, so that a naive "reserve-then-write" implementation without atomic locking
    /// would let a later reservation finish writing before an earlier one.
    /// </summary>
    private sealed class DelayingStubWebSocket : WebSocket
    {
        public readonly ConcurrentQueue<byte> WriteOrder = new();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            byte marker = buffer.Array![buffer.Offset];

            // Earlier markers delay longer, so a "reserve then release lock immediately" bug
            // would let later-reserved sends overtake earlier ones on the wire.
            await Task.Delay((10 - marker) * 5, cancellationToken).ConfigureAwait(false);

            WriteOrder.Enqueue(marker);
        }
    }

    [Fact]
    public async Task ConcurrentSends_PreserveMonotonicWireOrder_WithSendLockHeldAcrossReservation()
    {
        // Arrange
        DelayingStubWebSocket socket = new DelayingStubWebSocket();
        WebSocketConnection wsConn = new WebSocketConnection(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        WebSocketTransport transport = new WebSocketTransport();
        transport.Initialize(wsConn, socket);

        IConnection.ITransport iface = transport;

        // Act: fire N sends concurrently, each acquiring the send-lock scope, reserving a
        // sequence number, then writing a single marker byte equal to its reservation order.
        const int count = 8;
        Task[] tasks = new Task[count];
        for (int i = 0; i < count; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await using IAsyncDisposable scope = await iface.AcquireSendLockAsync(CancellationToken.None).ConfigureAwait(false);
                uint seq = iface.NextSendSequence();
                byte[] payload = [(byte)seq];
                await iface.SendAsyncCore(payload, CancellationToken.None).ConfigureAwait(false);
            });
        }

        await Task.WhenAll(tasks);

        // Assert: wire order must match reservation order (1..count), because the send lock
        // is held across both reservation and write.
        byte[] observed = [.. socket.WriteOrder];
        _ = observed.Should().BeInAscendingOrder();
        _ = observed.Should().Equal([.. Enumerable.Range(1, count).Select(x => (byte)x)]);
    }
}
