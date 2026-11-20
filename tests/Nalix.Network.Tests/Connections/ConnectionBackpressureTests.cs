// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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
using Nalix.Network.Options;
using TransportAsyncCallback = Nalix.Network.Internal.Transport.AsyncCallback;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// A frame the transport drops because a pending budget is full must be reported to the frame processor, so the
/// protocol can answer it instead of the client waiting for a reply that never comes (kitos#326).
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.AsyncCallback)]
public sealed class ConnectionBackpressureTests
{
    private static readonly IOpCodeExtractor s_extractor = new ZeroOpCodeExtractor();

    [Fact]
    public async Task OnFrameReceived_WhenPerConnectionBudgetIsFull_ReportsRejectedFrame()
    {
        EnsureRegistered();
        NetworkCallbackOptions options = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();
        (Socket client, Socket server, Socket listener) = await ConnectPairAsync();
        using (listener)
        using (client)
        using (server)
        using (Connection connection = new(server, s_extractor))
        using (BlockingProcessor processor = new())
        {
            connection.MessageProcessing += processor.ProcessFrame;

            for (int i = 0; i < options.MaxPerConnectionPendingPackets; i++)
            {
                _ = connection.OnFrameReceived(BufferLease.CopyFrom([0, 0])).Should().BeTrue();
            }

            bool handled = connection.OnFrameReceived(BufferLease.CopyFrom([9, 8, 7]));

            _ = handled.Should().BeFalse("the per-connection budget is full");
            _ = processor.Rejected.Should().ContainSingle().Which.Should().Equal(9, 8, 7);
        }
    }

    [Fact]
    public async Task OnFrameReceived_WhenPerAddressBudgetIsFull_ReportsRejectedFrame()
    {
        EnsureRegistered();
        NetworkCallbackOptions options = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();

        // Callbacks a previous test queued still hold the loopback address's slots until they run.
        await WaitForIdleDispatcherAsync();

        int previousPerIp = options.MaxPendingPerIp;
        int previousPerConnection = options.MaxPerConnectionPendingPackets;

        // Only the per-address layer may reject here. A queued callback holds its address slot until it starts, and
        // the blocking processor keeps thread-pool threads busy, so queued callbacks pile up and one is rejected.
        options.MaxPendingPerIp = 1;
        options.MaxPerConnectionPendingPackets = 1024;

        try
        {
            (Socket client, Socket server, Socket listener) = await ConnectPairAsync();
            using (listener)
            using (client)
            using (server)
            using (Connection connection = new(server, s_extractor))
            using (BlockingProcessor processor = new())
            {
                connection.MessageProcessing += processor.ProcessFrame;

                byte[]? rejected = null;
                for (int i = 0; i < 1000 && rejected is null; i++)
                {
                    byte[] frame = BitConverter.GetBytes(i);
                    if (!connection.OnFrameReceived(BufferLease.CopyFrom(frame)))
                    {
                        rejected = frame;
                    }
                }

                _ = rejected.Should().NotBeNull("the per-address budget must fill while callbacks are queued");
                _ = processor.Rejected.Should().ContainSingle().Which.Should().Equal(rejected!);
            }
        }
        finally
        {
            options.MaxPendingPerIp = previousPerIp;
            options.MaxPerConnectionPendingPackets = previousPerConnection;
        }
    }

    [Fact]
    public void TriggerProcessEvent_WhenPerConnectionBudgetIsFull_ReportsRejectedFrame()
    {
        EnsureRegistered();
        NetworkCallbackOptions options = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();

        using WebSocket socket = WebSocket.CreateFromStream(Stream.Null, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        using WebSocketConnection connection = new(socket, s_extractor, new IPEndPoint(IPAddress.Loopback, 0));
        using BlockingProcessor processor = new();
        connection.MessageProcessing += processor.ProcessFrame;

        for (int i = 0; i < options.MaxPerConnectionPendingPackets; i++)
        {
            connection.TriggerProcessEvent(BufferLease.CopyFrom([0, 0]));
        }

        connection.TriggerProcessEvent(BufferLease.CopyFrom([3, 2, 1]));

        _ = processor.Rejected.Should().ContainSingle().Which.Should().Equal(3, 2, 1);
    }

    private static async Task WaitForIdleDispatcherAsync()
    {
        for (int i = 0; i < 500 && TransportAsyncCallback.GetStatistics().PendingProcess > 0; i++)
        {
            await Task.Delay(20);
        }

        _ = TransportAsyncCallback.GetStatistics().PendingProcess.Should().Be(0, "no callback may be left over from an earlier test");
    }

    /// <summary>Holds every frame in <see cref="ProcessFrame"/> until disposed, so frames stay pending.</summary>
    private sealed class BlockingProcessor : IFrameProcessor, IDisposable
    {
        // Not disposed: callbacks still queued when the test ends wait on it after Dispose.
        private readonly ManualResetEventSlim _release = new(false);

        public List<byte[]> Rejected { get; } = [];

        public void ProcessFrame(object? sender, IConnectionEventArgs args) => _release.Wait(TimeSpan.FromSeconds(30));

        public void OnFrameRejected(IConnection connection, ReadOnlySpan<byte> frame)
        {
            lock (this.Rejected)
            {
                this.Rejected.Add(frame.ToArray());
            }
        }

        public void Dispose() => _release.Set();
    }

    private sealed class ZeroOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
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

    private static void EnsureRegistered()
    {
        InstanceManager.Instance.Register<ILogger>(NullLogger.Instance);
        _ = ConfigurationManager.Instance.Get<Nalix.Framework.Options.ObjectPoolOptions>();
        _ = InstanceManager.Instance.GetOrCreateInstance<BufferPoolManager>();
        InstanceManager.Instance.Register<Nalix.Abstractions.IObjectPoolManager>(ObjectPoolManager.Shared);
        InstanceManager.Instance.Register<ObjectPoolManager>(ObjectPoolManager.Shared);
    }
}
