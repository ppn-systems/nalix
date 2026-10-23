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

namespace Nalix.Integration.Tests;

[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketDeepVerificationTests : IDisposable
{
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-deep-{Guid.NewGuid():N}.private");

    public WebSocketDeepVerificationTests()
    {
        File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");
    }

    private static ushort GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class DeepTestProtocol : Protocol
    {
        private sealed class DeepFrameProcessor : IFrameProcessor
        {
            private readonly DeepTestProtocol _protocol;
            public DeepFrameProcessor(DeepTestProtocol protocol) => _protocol = protocol;
            public void ProcessFrame(object? sender, IConnectionEventArgs args) => _protocol.ProcessMessage(sender, args);
        }

        private sealed class StubOpCodeExtractor : IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }

        public override IFrameProcessor FrameProcessor { get; }
        public override IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public DeepTestProtocol()
        {
            FrameProcessor = new DeepFrameProcessor(this);
            this.SetConnectionAcceptance(true);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args)
        {
            if (args.Lease != null && args.Lease.Length > 0)
            {
                args.Connection.TCP.SendAsync(args.Lease.Memory).AsTask().Wait();
            }
        }
    }

    private static byte[] CreatePayload(byte[] data)
    {
        byte[] payload = new byte[10 + data.Length];
        payload[4] = 0x34;
        payload[5] = 0x12;
        data.CopyTo(payload.AsSpan(10));
        return payload;
    }

    [Fact]
    public async Task WebSocket_LargePayloadEcho_SucceedsWithoutCorruption()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<DeepTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new DeepTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            var options = new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false };
            using var client = new WebSocketSession(options);

            TaskCompletionSource<byte[]> echoTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            client.OnMessageReceived += (_, lease) =>
            {
                if (lease.Length > 10)
                {
                    byte[] data = lease.Span[10..].ToArray();
                    echoTcs.TrySetResult(data);
                }
            };

            await client.ConnectAsync();
            Assert.True(client.IsConnected);

            // 128KB payload
            byte[] sentData = new byte[128 * 1024];
            Random.Shared.NextBytes(sentData);

            byte[] fullPayload = CreatePayload(sentData);
            await client.SendAsync(fullPayload);

            Task completed = await Task.WhenAny(echoTcs.Task, Task.Delay(10000));
            Assert.Same(echoTcs.Task, completed);

            byte[] receivedData = await echoTcs.Task;
            Assert.Equal(sentData.Length, receivedData.Length);
            Assert.True(sentData.SequenceEqual(receivedData), "128KB WebSocket payload must match byte-for-byte");

            await client.DisconnectAsync();
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task WebSocket_MultiRoomGroupMulticast_IsolateMessagesByRoom()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        InMemoryGroupStore groupStore = new();

        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<DeepTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new DeepTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            var options = new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false };

            using var clientA = new WebSocketSession(options);
            using var clientB = new WebSocketSession(options);
            using var clientC = new WebSocketSession(options);

            ConcurrentBag<string> receivedA = new();
            ConcurrentBag<string> receivedB = new();
            ConcurrentBag<string> receivedC = new();

            clientA.OnMessageReceived += (_, lease) => { if (lease.Length > 10) receivedA.Add(System.Text.Encoding.UTF8.GetString(lease.Span[10..])); };
            clientB.OnMessageReceived += (_, lease) => { if (lease.Length > 10) receivedB.Add(System.Text.Encoding.UTF8.GetString(lease.Span[10..])); };
            clientC.OnMessageReceived += (_, lease) => { if (lease.Length > 10) receivedC.Add(System.Text.Encoding.UTF8.GetString(lease.Span[10..])); };

            // Connect client A
            await clientA.ConnectAsync();
            IConnection? connA = null;
            for (int i = 0; i < 50; i++)
            {
                connA = hub.ListConnections().FirstOrDefault();
                if (connA != null) break;
                await Task.Delay(20);
            }
            Assert.NotNull(connA);

            // Connect client B
            await clientB.ConnectAsync();
            IConnection? connB = null;
            for (int i = 0; i < 50; i++)
            {
                connB = hub.ListConnections().FirstOrDefault(c => c != connA);
                if (connB != null) break;
                await Task.Delay(20);
            }
            Assert.NotNull(connB);

            // Connect client C
            await clientC.ConnectAsync();
            IConnection? connC = null;
            for (int i = 0; i < 50; i++)
            {
                connC = hub.ListConnections().FirstOrDefault(c => c != connA && c != connB);
                if (connC != null) break;
                await Task.Delay(20);
            }
            Assert.NotNull(connC);

            // Add clientA to "alpha", clientB to ("alpha", "beta"), clientC to "beta"
            await groupStore.AddToGroupAsync("alpha", connA!);
            await groupStore.AddToGroupAsync("alpha", connB!);
            await groupStore.AddToGroupAsync("beta", connB!);
            await groupStore.AddToGroupAsync("beta", connC!);

            byte[] msgAlpha = CreatePayload(System.Text.Encoding.UTF8.GetBytes("ALPHA_MSG"));
            byte[] msgBeta = CreatePayload(System.Text.Encoding.UTF8.GetBytes("BETA_MSG"));

#pragma warning disable CS0618
            await hub.MulticastAsync(groupStore, "alpha", msgAlpha);
            await hub.MulticastAsync(groupStore, "beta", msgBeta);
#pragma warning restore CS0618

            await Task.Delay(1000);

            // Client A should receive ALPHA_MSG only
            Assert.Contains("ALPHA_MSG", receivedA);
            Assert.DoesNotContain("BETA_MSG", receivedA);

            // Client B should receive BOTH ALPHA_MSG and BETA_MSG
            Assert.Contains("ALPHA_MSG", receivedB);
            Assert.Contains("BETA_MSG", receivedB);

            // Client C should receive BETA_MSG only
            Assert.DoesNotContain("ALPHA_MSG", receivedC);
            Assert.Contains("BETA_MSG", receivedC);

            await clientA.DisconnectAsync();
            await clientB.DisconnectAsync();
            await clientC.DisconnectAsync();
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task WebSocket_GroupCleanup_OnDisconnectRemovesAllGroupEntries()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        InMemoryGroupStore groupStore = new();

        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<DeepTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new DeepTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            var options = new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false };
            using var client = new WebSocketSession(options);

            await client.ConnectAsync();
            IConnection? conn = null;
            for (int i = 0; i < 50; i++)
            {
                conn = hub.ListConnections().FirstOrDefault();
                if (conn != null) break;
                await Task.Delay(20);
            }
            Assert.NotNull(conn);

            await groupStore.AddToGroupAsync("room1", conn!);
            await groupStore.AddToGroupAsync("room2", conn!);

            Assert.Single(groupStore.GetGroupMembers("room1"));
            Assert.Single(groupStore.GetGroupMembers("room2"));

            // Disconnect client
            await client.DisconnectAsync();
            await Task.Delay(500);

            // Remove from all groups
            await groupStore.RemoveFromAllGroupsAsync(conn!);

            Assert.Empty(groupStore.GetGroupMembers("room1"));
            Assert.Empty(groupStore.GetGroupMembers("room2"));
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_certificatePath)) File.Delete(_certificatePath);
        }
        catch { }
        InstanceManager.Instance.Clear(dispose: false);
    }
}
