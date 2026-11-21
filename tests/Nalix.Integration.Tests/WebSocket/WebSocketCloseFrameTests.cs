// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Hosting;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Network.Connections;
using Nalix.Network.Options;
using Nalix.Network.Protocols;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Tests.Shared;
using Xunit;

namespace Nalix.Integration.Tests.WebSocket;

/// <summary>
/// A server-initiated close must reach the peer as a WebSocket close frame carrying an RFC 6455 status code,
/// not as a bare TCP drop (kitos#325).
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketCloseFrameTests : IDisposable
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly NetworkConfigSnapshot _configSnapshot = new();
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-close-{Guid.NewGuid():N}.private");

    public WebSocketCloseFrameTests()
    {
        File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
    }

    public void Dispose()
    {
        _configSnapshot.Dispose();
        try { File.Delete(_certificatePath); } catch (IOException) { }
    }

    [Fact]
    public async Task DeactivateAsync_WhenClientConnected_SendsEndpointUnavailableClose()
    {
        ushort port = (ushort)TestPorts.Next();
        using NetworkApplication app = this.BuildApp(port);
        await app.ActivateAsync();

        using ClientWebSocket client = await ConnectAsync(port);
        Task<WebSocketCloseStatus?> close = ReceiveCloseStatusAsync(client);

        await app.DeactivateAsync();

        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await close.WaitAsync(CloseTimeout));
    }

    [Fact]
    public async Task DeactivateAsync_WhenRequestInFlight_FailsRequestWithCloseStatus()
    {
        ushort port = (ushort)TestPorts.Next();
        using NetworkApplication app = this.BuildApp(port);
        await app.ActivateAsync();

        using WebSocketSession client = new(new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false });
        await client.ConnectAsync();

        // The test protocol never answers, so the request is still in flight when the host shuts down.
        using EchoPacket request = new() { Text = "in-flight" };
        Task<EchoPacket> inFlight = client.RequestAsync<EchoPacket>(request, new RequestOptions { TimeoutMs = 30_000 }).AsTask();

        await app.DeactivateAsync();

        NetworkException error = await Assert.ThrowsAsync<NetworkException>(() => inFlight.WaitAsync(CloseTimeout));
        Assert.Contains("close status 1001", DescribeChain(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_WhenMessageExceedsMaxMessageSize_SendsMessageTooBigClose()
    {
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().MaxMessageSize = 64;

        ushort port = (ushort)TestPorts.Next();
        using NetworkApplication app = this.BuildApp(port);
        await app.ActivateAsync();

        try
        {
            using ClientWebSocket client = await ConnectAsync(port);
            Task<WebSocketCloseStatus?> close = ReceiveCloseStatusAsync(client);

            await client.SendAsync(new byte[65], WebSocketMessageType.Binary, true, CancellationToken.None);

            Assert.Equal(WebSocketCloseStatus.MessageTooBig, await close.WaitAsync(CloseTimeout));
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    private static string DescribeChain(Exception error)
    {
        System.Text.StringBuilder text = new();
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            _ = text.Append(current.Message).Append(" | ");
        }

        return text.ToString();
    }

    private NetworkApplication BuildApp(ushort port)
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        _ = builder.UseSecureConnections(_certificatePath);
        _ = builder.UseConnectionHub(new ConnectionHub());
        _ = builder.MapWebSocket<CloseTestProtocol>()
                   .OnPort(port)
                   .WithPath("/ws/")
                   .WithFactory(_ => new CloseTestProtocol())
                   .Bind();
        return builder.Build();
    }

    private static async Task<ClientWebSocket> ConnectAsync(ushort port)
    {
        ClientWebSocket client = new();
        using CancellationTokenSource cts = new(CloseTimeout);
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/"), cts.Token);
        return client;
    }

    /// <summary>
    /// Reads until the server closes. Returns the close status of the close frame, or <see langword="null"/> when the
    /// socket was dropped without one.
    /// </summary>
    private static async Task<WebSocketCloseStatus?> ReceiveCloseStatusAsync(ClientWebSocket client)
    {
        byte[] buffer = new byte[1024];
        try
        {
            while (true)
            {
                WebSocketReceiveResult result = await client.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return result.CloseStatus;
                }
            }
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class CloseTestProtocol : Protocol
    {
        public CloseTestProtocol() => this.SetConnectionAcceptance(true);

        public override IFrameProcessor FrameProcessor { get; } = new NoopFrameProcessor();

        public override IOpCodeExtractor OpCodeExtractor { get; } = new ZeroOpCodeExtractor();

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }

        private sealed class NoopFrameProcessor : IFrameProcessor
        {
            public void ProcessFrame(object? sender, IConnectionEventArgs args) { }
        }

        private sealed class ZeroOpCodeExtractor : IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }
    }
}
