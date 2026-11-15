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
using Nalix.Tests.Shared;

namespace Nalix.Network.Tests.Listeners.WebListener;

[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketListenerDevOpsAuthTests : IDisposable
{
    private const string Token = "devops-test-token-0123456789";

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-devops-{Guid.NewGuid():N}.private");

    public WebSocketListenerDevOpsAuthTests()
    {
        File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");
    }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class DevOpsAuthTestProtocol : Protocol
    {
        private sealed class NoopFrameProcessor : IFrameProcessor
        {
            public void ProcessFrame(object? sender, IConnectionEventArgs args) { }
        }

        private sealed class StubOpCodeExtractor : IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }

        public override IFrameProcessor FrameProcessor { get; } = new NoopFrameProcessor();
        public override IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public DevOpsAuthTestProtocol()
        {
            this.SetConnectionAcceptance(true);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }

    /// <summary>
    /// Starts a WebSocket listener with the given DevOps bearer token (empty = not configured).
    /// The caller must dispose the returned application.
    /// </summary>
    private async Task<(Nalix.Hosting.NetworkApplication App, ushort Port)> StartAsync(string devOpsToken)
    {
        ushort port = (ushort)TestPorts.Next();
        var wsOpt = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        wsOpt.Host = "127.0.0.1";
        wsOpt.EnableDevOpsEndpoints = true;
        wsOpt.DevOpsBearerToken = devOpsToken;

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<DevOpsAuthTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new DevOpsAuthTestProtocol());

        NetworkApplication app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);
        return (app, port);
    }

    /// <summary>
    /// Sends a raw HTTP/1.1 GET and reads until the server closes the connection (responses use Connection: close).
    /// </summary>
    private static async Task<string> GetAsync(ushort port, string path, string? authorization = null)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port);

        StringBuilder request = new();
        request.Append("GET ").Append(path).Append(" HTTP/1.1\r\nHost: 127.0.0.1\r\n");
        if (authorization is not null)
        {
            request.Append("Authorization: ").Append(authorization).Append("\r\n");
        }
        request.Append("\r\n");
        await socket.SendAsync(Encoding.ASCII.GetBytes(request.ToString()), SocketFlags.None);

        var response = new StringBuilder();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            if (read == 0)
            {
                break;
            }
            response.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return response.ToString();
    }

    [Fact]
    public async Task Metrics_WithoutTokenConfigured_IsNotServed()
    {
        var (app, port) = await StartAsync(devOpsToken: string.Empty);
        try
        {
            string response = await GetAsync(port, "/metrics");

            Assert.DoesNotContain("nalix_active_connections", response);
            Assert.Contains("HTTP/1.1 404 Not Found", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Version_WithoutTokenConfigured_IsNotServed()
    {
        var (app, port) = await StartAsync(devOpsToken: string.Empty);
        try
        {
            string response = await GetAsync(port, "/version");

            Assert.DoesNotContain("\"version\"", response);
            Assert.Contains("HTTP/1.1 404 Not Found", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Metrics_WithTokenConfiguredAndNoCredentials_Returns401WithoutData()
    {
        var (app, port) = await StartAsync(Token);
        try
        {
            string response = await GetAsync(port, "/metrics");

            Assert.Contains("HTTP/1.1 401 Unauthorized", response);
            Assert.Contains("WWW-Authenticate: Bearer", response);
            Assert.DoesNotContain("nalix_active_connections", response);
            Assert.DoesNotContain("Access-Control-Allow-Origin", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Version_WithTokenConfiguredAndNoCredentials_Returns401WithoutData()
    {
        var (app, port) = await StartAsync(Token);
        try
        {
            string response = await GetAsync(port, "/version");

            Assert.Contains("HTTP/1.1 401 Unauthorized", response);
            Assert.DoesNotContain("\"version\"", response);
            Assert.DoesNotContain("Access-Control-Allow-Origin", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Theory]
    [InlineData("Bearer wrong-token-value-0000000")]
    [InlineData("Bearer devops-test-token-0123456780")]
    [InlineData("Basic ZGV2b3BzOnBhc3M=")]
    [InlineData("Bearer ")]
    public async Task Metrics_WithInvalidCredentials_Returns401(string authorization)
    {
        var (app, port) = await StartAsync(Token);
        try
        {
            string response = await GetAsync(port, "/metrics", authorization);

            Assert.Contains("HTTP/1.1 401 Unauthorized", response);
            Assert.DoesNotContain("nalix_active_connections", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bearer")]
    public async Task Metrics_WithCorrectBearerToken_Returns200WithDataAndNoWildcardCors(string scheme)
    {
        var (app, port) = await StartAsync(Token);
        try
        {
            string response = await GetAsync(port, "/metrics", $"{scheme} {Token}");

            Assert.Contains("HTTP/1.1 200 OK", response);
            Assert.Contains("nalix_active_connections", response);
            Assert.DoesNotContain("Access-Control-Allow-Origin", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Version_WithCorrectBearerToken_Returns200WithoutWildcardCors()
    {
        var (app, port) = await StartAsync(Token);
        try
        {
            string response = await GetAsync(port, "/version", $"Bearer {Token}");

            Assert.Contains("HTTP/1.1 200 OK", response);
            Assert.Contains("\"version\"", response);
            Assert.DoesNotContain("Access-Control-Allow-Origin", response);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Healthz_WithoutCredentials_Returns200WithoutWildcardCors()
    {
        var (app, port) = await StartAsync(Token);
        try
        {
            string response = await GetAsync(port, "/healthz");

            Assert.Contains("HTTP/1.1 200 OK", response);
            Assert.Contains("Healthy", response);
            Assert.DoesNotContain("Access-Control-Allow-Origin", response);
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
