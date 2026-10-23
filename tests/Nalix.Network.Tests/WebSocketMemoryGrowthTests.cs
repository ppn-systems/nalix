// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

namespace Nalix.Network.Tests;

[Collection(TestCollections.NetworkConfig)]
public class WebSocketMemoryGrowthTests : IDisposable
{
    private readonly DefaultBufferPoolManager _defaultManager = new();
    private readonly NetworkWebSocketOptions _options;

    public WebSocketMemoryGrowthTests()
    {
        _options = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        // Ensure standard settings
        _options.MaxMessageSize = 1024 * 1024; // 1MB
    }

    public void Dispose()
    {
        // Restore default pool manager
        BufferLease.ByteArrayPool.Configure(_defaultManager);
    }

    private sealed class StubOpCodeExtractor : Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private sealed class SpyBufferPoolManager : IBufferPoolManager
    {
        public ConcurrentBag<int> RentedSizes { get; } = new();
        public ConcurrentBag<byte[]> ReturnedBuffers { get; } = new();

        public byte[] Rent(int minimumLength = 256)
        {
            RentedSizes.Add(minimumLength);
            return ArrayPool<byte>.Shared.Rent(minimumLength);
        }

        public void Return(byte[]? array, bool arrayClear = false)
        {
            if (array != null)
            {
                ReturnedBuffers.Add(array);
                ArrayPool<byte>.Shared.Return(array, arrayClear);
            }
        }

        public string GenerateReport() => "";
        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer) { }
    }

    private sealed class DefaultBufferPoolManager : IBufferPoolManager
    {
        public byte[] Rent(int minimumLength = 256) => ArrayPool<byte>.Shared.Rent(minimumLength);
        public void Return(byte[]? array, bool arrayClear = false)
        {
            if (array != null)
            {
                ArrayPool<byte>.Shared.Return(array, arrayClear);
            }
        }
        public string GenerateReport() => "";
        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer) { }
    }

    private sealed class StubWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly System.Collections.Generic.Queue<(byte[] Data, bool EndOfMessage)> _receiveQueue = new();
        private bool _isAborted;
        private bool _isDisposed;

        public void EnqueueReceive(byte[] data, bool endOfMessage)
        {
            _receiveQueue.Enqueue((data, endOfMessage));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _isAborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _isAborted = true;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
            _isDisposed = true;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_isAborted || _isDisposed)
            {
                throw new WebSocketException(WebSocketError.InvalidState, "Socket is not open.");
            }

            if (!_receiveQueue.TryDequeue(out var item))
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            item.Data.AsSpan().CopyTo(buffer);
            return Task.FromResult(new WebSocketReceiveResult(item.Data.Length, WebSocketMessageType.Binary, item.EndOfMessage));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HANDLE_LARGE_MESSAGE_ASYNC_DoesNotRentMaxMessageSizeUpfront()
    {
        // Arrange
        var spy = new SpyBufferPoolManager();
        BufferLease.ByteArrayPool.Configure(spy);

        var socket = new StubWebSocket();
        // First fragment: 1KB. Total message is 2KB.
        var data1 = new byte[1024];
        var data2 = new byte[1024];
        socket.EnqueueReceive(data2, endOfMessage: true);

        var wsConn = new WebSocketConnection(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        var transport = new WebSocketTransport();
        transport.Initialize(wsConn, socket);

        // Act
        socket.EnqueueReceive(data1, endOfMessage: false);
        transport.BeginReceive(CancellationToken.None);

        // Wait a bit for the receive loop task to complete
        await Task.Delay(100);

        // Assert
        // We rented dynamic payload buffers:
        // - Initial capacity should be Math.Max(4096, initialBytes) = 4096.
        // - We did not rent MaxMessageSize (1MB = 1048576) upfront.
        spy.RentedSizes.Should().NotContain(x => x >= 1048576);
        spy.RentedSizes.Should().Contain(x => x == 4096);
    }

    [Fact]
    public async Task HANDLE_LARGE_MESSAGE_ASYNC_ExceedingMaxMessageSize_ThrowsException()
    {
        // Arrange
        var spy = new SpyBufferPoolManager();
        BufferLease.ByteArrayPool.Configure(spy);

        _options.MaxMessageSize = 2048; // Set max size to 2KB

        var socket = new StubWebSocket();
        var data1 = new byte[1500];
        var data2 = new byte[1000]; // Total 2500 bytes (exceeds 2048)
        socket.EnqueueReceive(data2, endOfMessage: true);

        var wsConn = new WebSocketConnection(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        var transport = new WebSocketTransport();
        transport.Initialize(wsConn, socket);

        // Act
        socket.EnqueueReceive(data1, endOfMessage: false);
        transport.BeginReceive(CancellationToken.None);

        await Task.Delay(100);

        // Assert
        // The connection should be disconnected due to the large message exception
        wsConn.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task HANDLE_LARGE_MESSAGE_ASYNC_ReturnsBufferOnExceptionBeforeTakeOwnership()
    {
        // Arrange
        var spy = new SpyBufferPoolManager();
        BufferLease.ByteArrayPool.Configure(spy);

        _options.MaxMessageSize = 2000;

        var socket = new StubWebSocket();
        var data1 = new byte[1500];
        // We will make the second receive throw a WebSocketException by leaving queue empty
        // so it fails inside HANDLE_LARGE_MESSAGE_ASYNC.
        var wsConn = new WebSocketConnection(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        var transport = new WebSocketTransport();
        transport.Initialize(wsConn, socket);

        // Act
        socket.EnqueueReceive(data1, endOfMessage: false);
        transport.BeginReceive(CancellationToken.None);

        await Task.Delay(100);

        // Assert
        // The socket aborted, so we expect the payload to be returned to the pool.
        spy.ReturnedBuffers.Should().NotBeEmpty();
        spy.ReturnedBuffers.Any(buf => buf.Length >= 4096).Should().BeTrue();
    }

    [Fact]
    public void WebSocketTransport_SequenceCounter_ReturnsSameReferenceAndPersistsState()
    {
        // Arrange
        var socket = new StubWebSocket();
        var wsConn = new WebSocketConnection(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        var transport = new WebSocketTransport();
        transport.Initialize(wsConn, socket);

        // Act & Assert
        // 1. Same reference check (verifies boxing is eliminated)
        ISequenceCounter rx1 = transport.ReceiveSequence;
        ISequenceCounter rx2 = transport.ReceiveSequence;
        rx1.Should().BeSameAs(rx2);

        ISequenceCounter tx1 = transport.SendSequence;
        ISequenceCounter tx2 = transport.SendSequence;
        tx1.Should().BeSameAs(tx2);

        // 2. State persistence checks
        rx1.IsValid(1).Should().BeTrue();
        rx1.UpdateTo(1);
        rx1.Current().Should().Be(1);

        // Next read of property should see the updated state (1)
        transport.ReceiveSequence.Current().Should().Be(1);
        transport.ReceiveSequence.IsValid(1).Should().BeFalse(); // Replay check

        // Send sequence increment/monotony
        tx1.Current().Should().Be(0);
        tx1.Next().Should().Be(1);
        tx1.Current().Should().Be(1);

        transport.SendSequence.Current().Should().Be(1);
        transport.SendSequence.Next().Should().Be(2);
    }
}

