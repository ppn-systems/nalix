// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.ProtocolFrames;
using Nalix.Hosting.Protocols;
using Nalix.Network.Connections;
using NSubstitute;
using Xunit;

namespace Nalix.Hosting.Tests.Protocols;

/// <summary>
/// A frame the transport rejected under backpressure gets a typed <c>FAIL / RATE_LIMITED / RETRY</c> directive that
/// carries the rejected request's sequence id and opcode, so the client can retry it (kitos#326).
/// </summary>
[Collection(TestCollections.RealServer)]
public sealed class DefaultFrameProcessorBackpressureTests : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnFrameRejected_WhenFrameHasHeader_RepliesRateLimitedWithSequenceId()
    {
        EnsurePoolConfigured();
        CapturingWebSocket socket = new();
        using WebSocketConnection connection = new(socket, Substitute.For<IOpCodeExtractor>(), new IPEndPoint(IPAddress.Loopback, 0));
        IFrameProcessor processor = new DefaultFrameProcessor(NullLogger.Instance, Substitute.For<IProtocol>());

        processor.OnFrameRejected(connection, Frame(opCode: 0x1234, sequenceId: 42));

        using Directive directive = Directive.Deserialize(await socket.NextMessageAsync().WaitAsync(ReplyTimeout));
        Assert.Equal(ControlType.FAIL, directive.Type);
        Assert.Equal(ProtocolReason.RATE_LIMITED, directive.Reason);
        Assert.Equal(ProtocolAdvice.RETRY, directive.Action);
        Assert.True(directive.Control.HasFlag(ControlFlags.IS_TRANSIENT));
        Assert.Equal((ushort)42, directive.Header.SequenceId);
        Assert.Equal(0x1234u, directive.Arg0);
    }

    [Fact]
    public async Task OnFrameRejected_WhenRepeatedWithinCooldown_RepliesOnce()
    {
        EnsurePoolConfigured();
        CapturingWebSocket socket = new();
        using WebSocketConnection connection = new(socket, Substitute.For<IOpCodeExtractor>(), new IPEndPoint(IPAddress.Loopback, 0));
        IFrameProcessor processor = new DefaultFrameProcessor(NullLogger.Instance, Substitute.For<IProtocol>());

        for (ushort i = 1; i <= 50; i++)
        {
            processor.OnFrameRejected(connection, Frame(opCode: 0x1234, sequenceId: i));
        }

        _ = await socket.NextMessageAsync().WaitAsync(ReplyTimeout);
        await Task.Delay(200);

        Assert.Equal(1, socket.MessageCount);
    }

    [Fact]
    public async Task OnFrameRejected_WhenFrameIsShorterThanHeader_SendsNothing()
    {
        EnsurePoolConfigured();
        CapturingWebSocket socket = new();
        using WebSocketConnection connection = new(socket, Substitute.For<IOpCodeExtractor>(), new IPEndPoint(IPAddress.Loopback, 0));
        IFrameProcessor processor = new DefaultFrameProcessor(NullLogger.Instance, Substitute.For<IProtocol>());

        processor.OnFrameRejected(connection, new byte[PacketHeader.Size - 1]);
        await Task.Delay(200);

        Assert.Equal(0, socket.MessageCount);
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);

    private static byte[] Frame(ushort opCode, ushort sequenceId)
    {
        PacketHeader header = default;
        header.OpCode = opCode;
        header.SequenceId = sequenceId;

        byte[] frame = new byte[PacketHeader.Size + 4];
        MemoryMarshal.Write(frame, in header);
        return frame;
    }

    private static void EnsurePoolConfigured()
    {
        try
        {
            var config = new Nalix.Framework.Options.ObjectPoolOptions { EnableDiagnostics = false };
            var poolManager = new Nalix.Framework.Memory.Objects.ObjectPoolManager(config);
            Nalix.Framework.Memory.Objects.ObjectPoolManager.Configure(poolManager);
            Nalix.Framework.Injection.InstanceManager.Instance.Register<IObjectPoolManager>(poolManager);
            Nalix.Framework.Injection.InstanceManager.Instance.Register<Nalix.Framework.Memory.Objects.ObjectPoolManager>(poolManager);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>An open WebSocket that records every message sent through it.</summary>
    private sealed class CapturingWebSocket : WebSocket
    {
        private readonly Queue<byte[]> _messages = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _count;

        public int MessageCount => Volatile.Read(ref _count);

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public async Task<byte[]> NextMessageAsync()
        {
            await _available.WaitAsync();
            lock (_messages)
            {
                return _messages.Dequeue();
            }
        }

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => new TaskCompletionSource<WebSocketReceiveResult>().Task.WaitAsync(cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_messages)
            {
                _messages.Enqueue(buffer.AsSpan().ToArray());
            }

            _ = Interlocked.Increment(ref _count);
            _ = _available.Release();
            return Task.CompletedTask;
        }
    }
}
