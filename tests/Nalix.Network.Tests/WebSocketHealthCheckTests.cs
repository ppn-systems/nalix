// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Network.Connections;
using Nalix.Network.Options;
using Nalix.Network.Protocols;
using Xunit;

namespace Nalix.Network.Tests;

[Collection("NetworkConfigTests")]
public sealed class WebSocketHealthCheckTests : IDisposable
{
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-health-{Guid.NewGuid():N}.private");

    public WebSocketHealthCheckTests()
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
    internal sealed class HealthTestProtocol : Protocol
    {
        private sealed class HealthFrameProcessor : IFrameProcessor
        {
            public void ProcessFrame(object? sender, IConnectionEventArgs args) { }
        }

        private sealed class StubOpCodeExtractor : IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }

        public override IFrameProcessor FrameProcessor { get; } = new HealthFrameProcessor();
        public override IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public HealthTestProtocol()
        {
            this.SetConnectionAcceptance(true);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/health")]
    [InlineData("/livez")]
    [InlineData("/readyz")]
    [InlineData("/startupz")]
    [InlineData("/ping")]
    public async Task WebSocketListener_GetHealthzEndpoint_Returns200OKHealthy(string path)
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<HealthTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new HealthTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);

            string requestLine = $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: kube-probe/1.28\r\n\r\n";
            await socket.SendAsync(Encoding.ASCII.GetBytes(requestLine), SocketFlags.None);

            byte[] buffer = new byte[512];
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            string response = Encoding.ASCII.GetString(buffer, 0, read);

            Assert.Contains("HTTP/1.1 200 OK", response);
            Assert.Contains("Healthy", response);
            Assert.Equal(0, hub.Count); // Health probes do not register as active WebSocket connections
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task WebSocketListener_GetVersionEndpoint_ReturnsCustomConfiguredVersion()
    {
        ushort port = GetFreePort();
        var wsOpt = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        wsOpt.Host = "127.0.0.1";
        wsOpt.ServerVersion = "2.5.0-release";

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<HealthTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new HealthTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);

            string requestLine = "GET /version HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
            await socket.SendAsync(Encoding.ASCII.GetBytes(requestLine), SocketFlags.None);

            byte[] buffer = new byte[512];
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            string response = Encoding.ASCII.GetString(buffer, 0, read);

            Assert.Contains("HTTP/1.1 200 OK", response);
            Assert.Contains("2.5.0-release", response);
            Assert.Contains("application/json", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task WebSocketListener_GetMetricsEndpoint_ReturnsPrometheusFormattedMetrics()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<HealthTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new HealthTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);

            string requestLine = "GET /metrics HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
            await socket.SendAsync(Encoding.ASCII.GetBytes(requestLine), SocketFlags.None);

            byte[] buffer = new byte[1024];
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            string response = Encoding.ASCII.GetString(buffer, 0, read);

            Assert.Contains("HTTP/1.1 200 OK", response);
            Assert.Contains("nalix_active_connections", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task WebSocketListener_CustomMapGetAndMapPost_AndOverrideMetrics_WorkEndToEnd()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<HealthTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new HealthTestProtocol())
               .MapGet("/status", static (in req, ref res) =>
               {
                   res.WriteJson("{\"region\":\"ap-southeast-1\",\"query\":\"" + Encoding.UTF8.GetString(req.Query) + "\"}");
               })
               .MapPost("/webhook", static (in req, ref res) =>
               {
                   res.StatusCode = 201;
                   res.Write("received:"u8);
                   res.Write(req.Body);
               })
               .MapGet("/metrics", static (in _, ref res) =>
               {
                   res.ContentType = "text/plain; version=0.0.4"u8;
                   res.Write("custom_game_rooms 42\n"u8);
               });

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        try
        {
            // 1. Test custom GET /status?mode=ranked
            using (Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                string req = "GET /status?mode=ranked HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
                await socket.SendAsync(Encoding.ASCII.GetBytes(req), SocketFlags.None);

                byte[] buffer = new byte[1024];
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
                string response = Encoding.UTF8.GetString(buffer, 0, read);

                Assert.Contains("HTTP/1.1 200 OK", response);
                Assert.Contains("application/json", response);
                Assert.Contains("\"region\":\"ap-southeast-1\"", response);
                Assert.Contains("\"query\":\"mode=ranked\"", response);
            }

            // 2. Test custom POST /webhook with body
            using (Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                string body = "{\"event\":\"reload\"}";
                string req = $"POST /webhook HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}";
                await socket.SendAsync(Encoding.ASCII.GetBytes(req), SocketFlags.None);

                byte[] buffer = new byte[1024];
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
                string response = Encoding.UTF8.GetString(buffer, 0, read);

                Assert.Contains("HTTP/1.1 201 Created", response);
                Assert.Contains("received:{\"event\":\"reload\"}", response);
            }

            // 3. Test overridden GET /metrics
            using (Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                string req = "GET /metrics HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
                await socket.SendAsync(Encoding.ASCII.GetBytes(req), SocketFlags.None);

                byte[] buffer = new byte[1024];
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
                string response = Encoding.UTF8.GetString(buffer, 0, read);

                Assert.Contains("HTTP/1.1 200 OK", response);
                Assert.Contains("custom_game_rooms 42", response);
            }

            // 4. Test 405 Method Not Allowed on POST /status
            using (Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                string req = "POST /status HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 0\r\n\r\n";
                await socket.SendAsync(Encoding.ASCII.GetBytes(req), SocketFlags.None);

                byte[] buffer = new byte[1024];
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
                string response = Encoding.UTF8.GetString(buffer, 0, read);

                Assert.Contains("HTTP/1.1 405 Method Not Allowed", response);
            }

            // 5. Test 404 Not Found on unknown non-WS GET request
            using (Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                string req = "GET /non-existent-route HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
                await socket.SendAsync(Encoding.ASCII.GetBytes(req), SocketFlags.None);

                byte[] buffer = new byte[1024];
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
                string response = Encoding.UTF8.GetString(buffer, 0, read);

                Assert.Contains("HTTP/1.1 404 Not Found", response);
            }

            Assert.Equal(0, hub.Count);
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
