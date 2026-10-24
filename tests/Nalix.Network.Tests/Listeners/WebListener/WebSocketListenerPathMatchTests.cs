// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Network.Connections;
using Nalix.Network.Options;
using Xunit;
using Nalix.Tests.Shared;

namespace Nalix.Network.Tests.Listeners.WebListener;

/// <summary>
/// Verifies that the upgrade request path must equal the configured path or continue it with <c>/</c> or <c>?</c>.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketListenerPathMatchTests : IDisposable
{
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-path-{Guid.NewGuid():N}.private");

    public WebSocketListenerPathMatchTests()
        => File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");

    [Theory]
    [InlineData("/ws", true)]
    [InlineData("/WS", true)]
    [InlineData("/ws/", true)]
    [InlineData("/ws?x=1", true)]
    [InlineData("/wsx", false)]
    [InlineData("/wsanything", false)]
    public async Task Upgrade_WithConfiguredPathWs_AcceptsOnlyExactOrSegmentMatch(string requestPath, bool accepted)
    {
        ushort port = (ushort)TestPorts.Next();
        var builder = NetworkApplication.CreateBuilder();
        builder.Configure<NetworkWebSocketOptions>(o => o.Host = "127.0.0.1");
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(new ConnectionHub());
        builder.MapWebSocket<WebSocketListenerHealthCheckTests.HealthTestProtocol>()
               .OnPort(port)
               .WithPath("/ws")
               .WithFactory(_ => new WebSocketListenerHealthCheckTests.HealthTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using Socket socket = await ConnectWithDeadlineAsync(port);
            string request =
                $"GET {requestPath} HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
            await socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None);

            byte[] buffer = new byte[512];
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            int read;
            try
            {
                read = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            }
            catch (SocketException)
            {
                read = 0;
            }

            if (accepted)
            {
                Assert.StartsWith("HTTP/1.1 101", Encoding.ASCII.GetString(buffer, 0, read), StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(0, read);
            }
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    private static async Task<Socket> ConnectWithDeadlineAsync(ushort port)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port);
                return socket;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                socket.Dispose();
                await Task.Delay(20);
            }
        }
    }

    public void Dispose()
    {
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
