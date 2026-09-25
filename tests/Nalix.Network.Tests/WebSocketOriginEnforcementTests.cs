// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Network.Connections;
using Nalix.Network.Options;
using Xunit;

namespace Nalix.Network.Tests;

/// <summary>
/// End-to-end CSWSH enforcement on the real WebSocket listener: a disallowed or (when configured)
/// missing <c>Origin</c> must get <c>403 Forbidden</c> before the upgrade and never register a connection.
/// </summary>
[Collection("NetworkConfigTests")]
public sealed class WebSocketOriginEnforcementTests : IDisposable
{
    private const string Allowed = "https://app.example.com";

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-origin-{Guid.NewGuid():N}.private");

    public WebSocketOriginEnforcementTests()
    {
        File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");
    }

    [Theory]
    [InlineData("https://app.example.com", true, "101")]
    [InlineData("HTTPS://APP.Example.COM", true, "101")]
    [InlineData("https://evil.example", true, "403")]
    [InlineData("http://app.example.com", true, "403")]
    [InlineData("https://app.example.com:8443", true, "403")]
    [InlineData(null, true, "101")]
    [InlineData(null, false, "403")]
    public async Task Upgrade_WithAllowlist_EnforcesOrigin(string? origin, bool allowMissing, string expectedStatus)
    {
        string response = await this.SendUpgradeAsync(Allowed, allowMissing, origin);

        Assert.StartsWith($"HTTP/1.1 {expectedStatus}", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upgrade_EmptyAllowlist_AcceptsAnyOrigin_LegacyDefault()
    {
        string response = await this.SendUpgradeAsync(string.Empty, allowMissing: false, origin: "https://evil.example");

        Assert.StartsWith("HTTP/1.1 101", response, StringComparison.Ordinal);
    }

    private async Task<string> SendUpgradeAsync(string allowedOrigins, bool allowMissing, string? origin)
    {
        ushort port = GetFreePort();
        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.Configure<NetworkWebSocketOptions>(o =>
        {
            o.Host = "127.0.0.1";
            o.AllowedOrigins = allowedOrigins;
            o.AllowMissingOrigin = allowMissing;
        });
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<WebSocketHealthCheckTests.HealthTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new WebSocketHealthCheckTests.HealthTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(300);

        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);

            string request =
                "GET /ws/ HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" +
                (origin is null ? string.Empty : $"Origin: {origin}\r\n") + "\r\n";
            await socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None);

            byte[] buffer = new byte[512];
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            string response = Encoding.ASCII.GetString(buffer, 0, read);

            if (response.StartsWith("HTTP/1.1 403", StringComparison.Ordinal))
            {
                // Rejected before upgrade: server closes and no connection/session is registered.
                Assert.Equal(0, hub.Count);
                int tail = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
                Assert.Equal(0, tail);
            }

            return response;
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    private static ushort GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        NetworkWebSocketOptions options = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        options.AllowedOrigins = string.Empty;
        options.AllowMissingOrigin = true;
        try
        {
            if (File.Exists(_certificatePath))
            {
                File.Delete(_certificatePath);
            }
        }
        catch { }
        InstanceManager.Instance.Clear(dispose: false);
    }
}
