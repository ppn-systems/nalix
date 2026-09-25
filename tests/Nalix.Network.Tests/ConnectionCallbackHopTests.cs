// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
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
using TransportAsyncCallback = Nalix.Network.Internal.Transport.AsyncCallback;

namespace Nalix.Network.Tests;

/// <summary>
/// Covers the thread-pool hop shortcuts on the connection callback path:
/// no post-send work item without a <c>MessageProcessed</c> subscriber, and inline frame
/// processing for processors that declare <see cref="IFrameProcessor.SupportsInlineProcessing"/>.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(AsyncCallbackSerialGroup.Name)]
public sealed class ConnectionCallbackHopTests
{
    private static readonly IOpCodeExtractor s_extractor = new TestOpCodeExtractor();

    [Fact]
    public async Task Send_WithoutMessageProcessedSubscriber_QueuesNoPostCallback()
    {
        EnsureLoggerRegistered();
        (Socket client, Socket server, Socket listener) = await ConnectPairAsync();
        using (listener)
        using (client)
        using (server)
        using (Connection connection = new(server, s_extractor))
        {
            TransportAsyncCallback.ResetStatistics();

            connection.TCP.Send([7, 8, 9, 10]);

            byte[] buffer = new byte[32];
            int read = await client.ReceiveAsync(buffer, SocketFlags.None);
            _ = read.Should().BeGreaterThan(0);

            await Task.Delay(50);

            Nalix.Network.Internal.Transport.AsyncCallbackMetrics stats = TransportAsyncCallback.GetStatistics();
            _ = stats.Total.Should().Be(0);
            _ = stats.PendingPost.Should().Be(0);
        }
    }

    [Fact]
    public async Task OnFrameReceived_InlineCapableProcessor_RunsSynchronouslyOnCaller()
    {
        EnsureLoggerRegistered();
        (Socket client, Socket server, Socket listener) = await ConnectPairAsync();
        using (listener)
        using (client)
        using (server)
        using (Connection connection = new(server, s_extractor))
        {
            RecordingProcessor processor = new(inline: true);
            connection.MessageProcessing += processor.ProcessFrame;
            TransportAsyncCallback.ResetStatistics();

            bool handled = connection.OnFrameReceived(BufferLease.CopyFrom([1, 2, 3]));

            _ = handled.Should().BeTrue();
            _ = processor.Calls.Should().Be(1);
            _ = processor.ThreadId.Should().Be(System.Environment.CurrentManagedThreadId);
            _ = processor.LastLength.Should().Be(3);
            _ = connection.PendingPackets.Should().Be(0);
            _ = TransportAsyncCallback.GetStatistics().PendingProcess.Should().Be(0);
        }
    }

    [Fact]
    public async Task OnFrameReceived_DefaultProcessor_IsStillQueued()
    {
        EnsureLoggerRegistered();
        (Socket client, Socket server, Socket listener) = await ConnectPairAsync();
        using (listener)
        using (client)
        using (server)
        using (Connection connection = new(server, s_extractor))
        {
            RecordingProcessor processor = new(inline: false);
            connection.MessageProcessing += processor.ProcessFrame;

            bool handled = connection.OnFrameReceived(BufferLease.CopyFrom([1, 2, 3]));
            _ = handled.Should().BeTrue();

            await processor.Observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            _ = processor.Calls.Should().Be(1);
        }
    }

    [Fact]
    public void CanRunInline_RequiresSingleInlineCapableProcessor()
    {
        RecordingProcessor inline = new(inline: true);
        RecordingProcessor queued = new(inline: false);

        EventHandler<IConnectionEventArgs> single = inline.ProcessFrame;
        EventHandler<IConnectionEventArgs> multi = inline.ProcessFrame;
        multi += queued.ProcessFrame;
        static void lambda(object? _1, IConnectionEventArgs _2) { }

        _ = TransportAsyncCallback.CanRunInline(single).Should().BeTrue();
        _ = TransportAsyncCallback.CanRunInline(multi).Should().BeFalse();
        _ = TransportAsyncCallback.CanRunInline(queued.ProcessFrame).Should().BeFalse();
        _ = TransportAsyncCallback.CanRunInline(lambda).Should().BeFalse();
        _ = TransportAsyncCallback.CanRunInline(null).Should().BeFalse();
    }

    private sealed class RecordingProcessor(bool inline) : IFrameProcessor
    {
        public int Calls;
        public int ThreadId;
        public int LastLength;
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SupportsInlineProcessing => inline;

        public void ProcessFrame(object? sender, IConnectionEventArgs args)
        {
            ThreadId = System.Environment.CurrentManagedThreadId;
            LastLength = args.Lease?.Length ?? -1;
            _ = Interlocked.Increment(ref Calls);
            _ = this.Observed.TrySetResult();
        }
    }

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload) : (ushort)0;
    }

    private static async Task<(Socket Client, Socket Server, Socket Listener)> ConnectPairAsync()
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Task<Socket> accept = listener.AcceptAsync();
        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);
        Socket server = await accept;
        return (client, server, listener);
    }

    private static void EnsureLoggerRegistered()
    {
        InstanceManager.Instance.Register<ILogger>(NullLogger.Instance);
        _ = ConfigurationManager.Instance.Get<Nalix.Framework.Options.ObjectPoolOptions>();
        _ = InstanceManager.Instance.GetOrCreateInstance<BufferPoolManager>();
        _ = InstanceManager.Instance.GetOrCreateInstance<ObjectPoolManager>();
    }
}
