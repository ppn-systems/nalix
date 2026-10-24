// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Network.Connections;
using Nalix.Network.Options;
using Nalix.Network.Protocols;
using Nalix.Runtime.Groups;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Xunit;
using Nalix.Integration.Tests.TestSupport;

namespace Nalix.Integration.Tests.WebSocket;

[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketGroupTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-group-{Guid.NewGuid():N}.private");

    public WebSocketGroupTests()
    {
        EnsureCertificate();
    }

    private void EnsureCertificate()
        => File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");

    private static ushort GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class GroupIntegrationProtocol : Protocol
    {
        public readonly ConcurrentBag<IConnection> Connections = new();

        private sealed class WebTestFrameProcessor : IFrameProcessor
        {
            private readonly GroupIntegrationProtocol _protocol;
            public WebTestFrameProcessor(GroupIntegrationProtocol protocol) => _protocol = protocol;
            public void ProcessFrame(object? sender, IConnectionEventArgs args) => _protocol.ProcessMessage(sender, args);
        }

        private sealed class StubOpCodeExtractor : IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }

        public override IFrameProcessor FrameProcessor { get; }
        public override IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public GroupIntegrationProtocol()
        {
            FrameProcessor = new WebTestFrameProcessor(this);
            this.SetConnectionAcceptance(true);
        }

        public override void OnAccept(IConnection connection, CancellationToken cancellationToken = default)
        {
            base.OnAccept(connection, cancellationToken);
            Connections.Add(connection);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args)
        {
            if (args.Lease != null && args.Lease.Length > 0)
            {
                args.Connection.TCP.SendAsync(args.Lease.Memory).AsTask().Wait();
            }
        }
    }

    private static byte[] CreatePayload(string text)
    {
        byte[] rawText = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[10 + rawText.Length];
        payload[4] = 0x34;
        payload[5] = 0x12;
        rawText.CopyTo(payload.AsSpan(10));
        return payload;
    }

    [Fact]
    public async Task Broadcast_WithMultipleClients_ReachesAllClients()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        GroupIntegrationProtocol protocol = new();

        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<GroupIntegrationProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => protocol);

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            var options = new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false };

            using var client1 = new WebSocketSession(options);
            using var client2 = new WebSocketSession(options);

            TaskCompletionSource<string> client1Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> client2Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            client1.OnMessageReceived += (_, lease) =>
            {
                if (lease.Length > 10) client1Tcs.TrySetResult(System.Text.Encoding.UTF8.GetString(lease.Span[10..]));
            };
            client2.OnMessageReceived += (_, lease) =>
            {
                if (lease.Length > 10) client2Tcs.TrySetResult(System.Text.Encoding.UTF8.GetString(lease.Span[10..]));
            };

            await client1.ConnectAsync();
            await client2.ConnectAsync();

            await Task.Delay(500);
            Assert.Equal(2, hub.Count);

            byte[] broadcastMessage = CreatePayload("Hello Everyone");
#pragma warning disable CS0618
            await hub.BroadcastAsync(broadcastMessage);
#pragma warning restore CS0618

            await Task.WhenAll(client1Tcs.Task, client2Tcs.Task).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("Hello Everyone", await client1Tcs.Task);
            Assert.Equal("Hello Everyone", await client2Tcs.Task);

            await client1.DisconnectAsync();
            await client2.DisconnectAsync();
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Multicast_ToGroup_ReachesOnlyGroupMembers()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        GroupIntegrationProtocol protocol = new();
        InMemoryGroupStore groupStore = new();

        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<GroupIntegrationProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => protocol);

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            var options = new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false };

            using var clientInGroup = new WebSocketSession(options);
            using var clientOutGroup = new WebSocketSession(options);

            TaskCompletionSource<string> inGroupTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> outGroupTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            clientInGroup.OnMessageReceived += (_, lease) =>
            {
                if (lease.Length > 10) inGroupTcs.TrySetResult(System.Text.Encoding.UTF8.GetString(lease.Span[10..]));
            };
            clientOutGroup.OnMessageReceived += (_, lease) =>
            {
                if (lease.Length > 10) outGroupTcs.TrySetResult(System.Text.Encoding.UTF8.GetString(lease.Span[10..]));
            };

            await clientInGroup.ConnectAsync();

            // Wait for clientInGroup connection to land on server hub
            IConnection? serverConnInGroup = null;
            for (int i = 0; i < 100; i++)
            {
                serverConnInGroup = hub.ListConnections().FirstOrDefault();
                if (serverConnInGroup != null) break;
                await Task.Delay(20);
            }
            Assert.NotNull(serverConnInGroup);

            await clientOutGroup.ConnectAsync();

            // Wait for second connection
            for (int i = 0; i < 100 && hub.Count < 2; i++)
            {
                await Task.Delay(20);
            }
            Assert.Equal(2, hub.Count);

            // Add specifically clientInGroup's server-side connection to room
            await groupStore.AddToGroupAsync("vip_room", serverConnInGroup!);

            byte[] multicastMessage = CreatePayload("VIP Only");
#pragma warning disable CS0618
            await hub.MulticastAsync(groupStore, "vip_room", multicastMessage);
#pragma warning restore CS0618

            Task completed = await Task.WhenAny(inGroupTcs.Task, Task.Delay(2000));
            Assert.Same(inGroupTcs.Task, completed);

            Assert.False(outGroupTcs.Task.IsCompleted, "Client outside group must not receive multicast message");

            await clientInGroup.DisconnectAsync();
            await clientOutGroup.DisconnectAsync();
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose()
    {
        _configSnapshot.Dispose();
        try
        {
            if (File.Exists(_certificatePath)) File.Delete(_certificatePath);
        }
        catch { }
        InstanceManager.Instance.Clear(dispose: false);
    }
}
