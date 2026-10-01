#if DEBUG
// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class TransportSendLockTests : IDisposable
{
    private readonly Socket _socket;
    private readonly Connection _connection;
    private readonly DelayingStubWebSocket _webSocket;
    private readonly WebSocketConnection _webSocketConnection;

    public TransportSendLockTests()
    {
        EnsureRegistered();

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _connection = new Connection(_socket, new DummyOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));

        _webSocket = new DelayingStubWebSocket();
        _webSocketConnection = new WebSocketConnection(_webSocket, new DummyOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
    }

    public void Dispose()
    {
        _connection.Dispose();
        _socket.Dispose();
        _webSocketConnection.Dispose();
        _webSocket.Dispose();
    }

    [Fact]
    public async Task SocketTcpTransport_AcquireSendLockAsync_FastPath_CompletesSynchronouslyAndReturnsCachedScope()
    {
        IConnection.ITransport transport = _connection.TCP;

        // Act: Uncontended acquire must complete synchronously on the fast-path
        ValueTask<IAsyncDisposable> vt = transport.AcquireSendLockAsync(CancellationToken.None);

        vt.IsCompletedSuccessfully.Should().BeTrue("uncontended acquire must use synchronous fast-path without state machine");
        IAsyncDisposable scope = vt.Result;
        scope.Should().NotBeNull();

        // Release the scope
        await scope.DisposeAsync();

        // Subsequent acquire must succeed synchronously and yield the exact same cached scope instance
        ValueTask<IAsyncDisposable> vt2 = transport.AcquireSendLockAsync(CancellationToken.None);
        vt2.IsCompletedSuccessfully.Should().BeTrue();
        vt2.Result.Should().BeSameAs(scope, "cached SendLockScope must be reused across send operations to prevent allocations");

        await vt2.Result.DisposeAsync();
    }

    [Fact]
    public async Task SocketTcpTransport_AcquireSendLockAsync_WhenContended_AwaitsUntilFirstScopeDisposed()
    {
        IConnection.ITransport transport = _connection.TCP;

        // Hold first lock
        ValueTask<IAsyncDisposable> vt1 = transport.AcquireSendLockAsync(CancellationToken.None);
        vt1.IsCompletedSuccessfully.Should().BeTrue();
        IAsyncDisposable scope1 = vt1.Result;

        // Second acquire is contended
        ValueTask<IAsyncDisposable> vt2 = transport.AcquireSendLockAsync(CancellationToken.None);
        vt2.IsCompletedSuccessfully.Should().BeFalse("lock is held by scope1, so second acquire must wait asynchronously");

        Task<IAsyncDisposable> waitTask = vt2.AsTask();
        waitTask.IsCompleted.Should().BeFalse();

        // Dispose scope1 to release lock
        await scope1.DisposeAsync();

        // Second acquire should now complete
        IAsyncDisposable scope2 = await waitTask;
        scope2.Should().BeSameAs(scope1, "reused scope should be handed to the next waiter");

        await scope2.DisposeAsync();
    }

    [Fact]
    public async Task SocketTcpTransport_AcquireSendLockAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        IConnection.ITransport transport = _connection.TCP;

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () =>
        {
            IAsyncDisposable scope = await transport.AcquireSendLockAsync(cts.Token);
            await scope.DisposeAsync();
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Lock must still be uncorrupted and free for subsequent acquires
        ValueTask<IAsyncDisposable> vt = transport.AcquireSendLockAsync(CancellationToken.None);
        vt.IsCompletedSuccessfully.Should().BeTrue();
        await vt.Result.DisposeAsync();
    }

    [Fact]
    public async Task WebSocketTransport_AcquireSendLockAsync_FastPath_CompletesSynchronouslyAndReturnsCachedScope()
    {
        IConnection.ITransport transport = _webSocketConnection.TCP;

        // Act: Uncontended acquire must complete synchronously on the fast-path
        ValueTask<IAsyncDisposable> vt = transport.AcquireSendLockAsync(CancellationToken.None);

        vt.IsCompletedSuccessfully.Should().BeTrue("uncontended acquire must use synchronous fast-path without state machine");
        IAsyncDisposable scope = vt.Result;
        scope.Should().NotBeNull();

        // Release the scope
        await scope.DisposeAsync();

        // Subsequent acquire must succeed synchronously and yield the exact same cached scope instance
        ValueTask<IAsyncDisposable> vt2 = transport.AcquireSendLockAsync(CancellationToken.None);
        vt2.IsCompletedSuccessfully.Should().BeTrue();
        vt2.Result.Should().BeSameAs(scope, "cached SendLockScope must be reused across send operations to prevent allocations");

        await vt2.Result.DisposeAsync();
    }

    [Fact]
    public async Task WebSocketTransport_AcquireSendLockAsync_WhenContended_AwaitsUntilFirstScopeDisposed()
    {
        IConnection.ITransport transport = _webSocketConnection.TCP;

        // Hold first lock
        ValueTask<IAsyncDisposable> vt1 = transport.AcquireSendLockAsync(CancellationToken.None);
        vt1.IsCompletedSuccessfully.Should().BeTrue();
        IAsyncDisposable scope1 = vt1.Result;

        // Second acquire is contended
        ValueTask<IAsyncDisposable> vt2 = transport.AcquireSendLockAsync(CancellationToken.None);
        vt2.IsCompletedSuccessfully.Should().BeFalse("lock is held by scope1, so second acquire must wait asynchronously");

        Task<IAsyncDisposable> waitTask = vt2.AsTask();
        waitTask.IsCompleted.Should().BeFalse();

        // Dispose scope1 to release lock
        await scope1.DisposeAsync();

        // Second acquire should now complete
        IAsyncDisposable scope2 = await waitTask;
        scope2.Should().BeSameAs(scope1, "reused scope should be handed to the next waiter");

        await scope2.DisposeAsync();
    }

    [Fact]
    public async Task WebSocketTransport_AcquireSendLockAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        IConnection.ITransport transport = _webSocketConnection.TCP;

        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () =>
        {
            IAsyncDisposable scope = await transport.AcquireSendLockAsync(cts.Token);
            await scope.DisposeAsync();
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Lock must still be uncorrupted and free for subsequent acquires
        ValueTask<IAsyncDisposable> vt = transport.AcquireSendLockAsync(CancellationToken.None);
        vt.IsCompletedSuccessfully.Should().BeTrue();
        await vt.Result.DisposeAsync();
    }

    [Fact]
    public async Task SocketTcpTransport_ResetForPool_ReinitializesCachedScopeCleanly()
    {
        SocketTcpTransport transport = new();
        SocketConnection socketConn = ObjectPoolManager.Shared.Get<SocketConnection>();
        socketConn.Initialize(_socket, _connection);
        transport.Initialize(_connection, socketConn);

        IConnection.ITransport iface = transport;

        // Acquire lock
        ValueTask<IAsyncDisposable> vt1 = iface.AcquireSendLockAsync(CancellationToken.None);
        vt1.IsCompletedSuccessfully.Should().BeTrue();
        IAsyncDisposable scope1 = vt1.Result;
        await scope1.DisposeAsync();

        // Reset for pool
        transport.ResetForPool();

        // Re-initialize
        transport.Initialize(_connection, socketConn);

        // Acquire lock again
        ValueTask<IAsyncDisposable> vt2 = iface.AcquireSendLockAsync(CancellationToken.None);
        vt2.IsCompletedSuccessfully.Should().BeTrue();
        IAsyncDisposable scope2 = vt2.Result;
        scope2.Should().NotBeNull();
        await scope2.DisposeAsync();

        transport.ResetForPool();
        socketConn.ResetForPool();
    }

    [Fact]
    public async Task WebSocketTransport_ResetForPool_ReinitializesCachedScopeCleanly()
    {
        WebSocketTransport transport = new();
        transport.Initialize(_webSocketConnection, _webSocket);

        IConnection.ITransport iface = transport;

        // Acquire lock
        ValueTask<IAsyncDisposable> vt1 = iface.AcquireSendLockAsync(CancellationToken.None);
        vt1.IsCompletedSuccessfully.Should().BeTrue();
        IAsyncDisposable scope1 = vt1.Result;
        await scope1.DisposeAsync();

        // Reset for pool
        transport.ResetForPool();

        // Re-initialize
        transport.Initialize(_webSocketConnection, _webSocket);

        // Acquire lock again
        ValueTask<IAsyncDisposable> vt2 = iface.AcquireSendLockAsync(CancellationToken.None);
        vt2.IsCompletedSuccessfully.Should().BeTrue();
        IAsyncDisposable scope2 = vt2.Result;
        scope2.Should().NotBeNull();
        await scope2.DisposeAsync();

        transport.ResetForPool();
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private sealed class DelayingStubWebSocket : WebSocket
    {
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
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static void EnsureRegistered()
    {
        InstanceManager.Instance.Register<ILogger>(NullLogger.Instance);
        _ = ConfigurationManager.Instance.Get<Nalix.Framework.Options.ObjectPoolOptions>();
        _ = InstanceManager.Instance.GetOrCreateInstance<BufferPoolManager>();
        _ = InstanceManager.Instance.GetOrCreateInstance<ObjectPoolManager>();
    }
}
#endif
