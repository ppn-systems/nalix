// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Xunit;

namespace Nalix.Integration.Tests;

[Collection("NetworkConfigTests")]
public sealed class WebSocketAdvancedEdgeCasesTests : IDisposable
{
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-adv-{Guid.NewGuid():N}.private");

    public WebSocketAdvancedEdgeCasesTests()
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
    internal sealed class EdgeTestProtocol : Protocol
    {
        private sealed class EdgeFrameProcessor : IFrameProcessor
        {
            public void ProcessFrame(object? sender, IConnectionEventArgs args) { }
        }

        private sealed class StubOpCodeExtractor : IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }

        public override IFrameProcessor FrameProcessor { get; } = new EdgeFrameProcessor();
        public override IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public EdgeTestProtocol()
        {
            this.SetConnectionAcceptance(true);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }

    [Fact]
    public async Task WebSocket_SlowHandshakeTimeout_ClosesIncompleteUpgradeSocket()
    {
        ushort port = GetFreePort();
        var wsOptions = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        wsOptions.Host = "127.0.0.1";
        wsOptions.HandshakeTimeoutMs = 500; // 500ms short handshake timeout

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<EdgeTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new EdgeTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            // Open raw socket and send INCOMPLETE HTTP header (Slowloris attack simulation)
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);
            await socket.SendAsync(Encoding.ASCII.GetBytes("GET /ws/ HTTP/1.1\r\nHost: 127.0.0.1\r\n"), SocketFlags.None);

            // Wait for handshake sweeper to kick the incomplete connection
            byte[] buf = new byte[128];
            int read = 0;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                read = await socket.ReceiveAsync(buf, SocketFlags.None, cts.Token);
            }
            catch (SocketException) { }
            catch (OperationCanceledException) { }

            Assert.Equal(0, read);
            Assert.Equal(0, hub.Count);
        }
        finally
        {
            wsOptions.HandshakeTimeoutMs = 10000;
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task WebSocket_MaxConnectionsThreshold_RejectsExcessConnections()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 1;
        // The test clients are on loopback, which is exempt from per-IP quotas by default.
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().ExemptLoopback = false;

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<EdgeTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new EdgeTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            var options = new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false };

            using var client1 = new WebSocketSession(options);
            await client1.ConnectAsync();
            Assert.True(client1.IsConnected);

            for (int i = 0; i < 50 && hub.Count == 0; i++)
            {
                await Task.Delay(10);
            }
            Assert.Equal(1, hub.Count);

            // Second client should be rejected by MaxConnections limit = 1
            using var client2 = new WebSocketSession(options);
            Func<Task> connectAction = async () => await client2.ConnectAsync();
            await Assert.ThrowsAnyAsync<Exception>(connectAction);

            Assert.Equal(1, hub.Count);

            await client1.DisconnectAsync();
        }
        finally
        {
            ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 1000;
            ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().ExemptLoopback = true;
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task WebSocket_PacketOverflowPolicy_DisconnectsAbusiveClient()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<EdgeTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new EdgeTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            var options = new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false };
            using var client = new WebSocketSession(options);

            TaskCompletionSource<bool> disconnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnDisconnected += (_, _) => disconnectTcs.TrySetResult(true);

            await client.ConnectAsync();
            Assert.True(client.IsConnected);

            for (int i = 0; i < 50 && hub.Count == 0; i++)
            {
                await Task.Delay(10);
            }
            Assert.Equal(1, hub.Count);

            var serverConn = hub.ListConnections().FirstOrDefault() as WebSocketConnection;
            Assert.NotNull(serverConn);

            // Trigger error threshold
            for (int i = 0; i < 60; i++)
            {
                serverConn!.IncrementErrorCount();
            }

            Task completed = await Task.WhenAny(disconnectTcs.Task, Task.Delay(5000));
            Assert.Same(disconnectTcs.Task, completed);
            Assert.False(client.IsConnected, "Connection exceeding error threshold must be disconnected");
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
