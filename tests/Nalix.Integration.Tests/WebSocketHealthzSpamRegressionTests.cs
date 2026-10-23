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

namespace Nalix.Integration.Tests;

/// <summary>
/// Reproduces the reported production repro exactly: plain HTTP GET /healthz requests,
/// each on its own short-lived TCP connection (connect, send, receive, close -- no WS
/// handshake, no stalled sockets), repeated sequentially many times against the SAME
/// listener instance. Reported thresholds: ~20 cumulative survives, ~40+ cumulative kills
/// the listener permanently (no self-recovery).
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketHealthzSpamRegressionTests : IDisposable
{
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-healthz-spam-{Guid.NewGuid():N}.private");

    public WebSocketHealthzSpamRegressionTests()
        => File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");

    private static ushort GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class PlainWsProtocol : Protocol
    {
        private sealed class NoopFrameProcessor : IFrameProcessor
        {
            public void ProcessFrame(object? sender, IConnectionEventArgs args) { }
        }

        private sealed class NoopOpCodeExtractor : IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }

        public override IFrameProcessor FrameProcessor { get; } = new NoopFrameProcessor();
        public override IOpCodeExtractor OpCodeExtractor { get; } = new NoopOpCodeExtractor();

        public PlainWsProtocol() => this.SetConnectionAcceptance(true);

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }

    private static async Task<bool> ProbeHealthzAsync(ushort port)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ReceiveTimeout = 3000;
        socket.SendTimeout = 3000;
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port);
            byte[] request = Encoding.ASCII.GetBytes("GET /healthz HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
            await socket.SendAsync(request, SocketFlags.None);

            byte[] buffer = new byte[256];
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            return read > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sequential (1-at-a-time, no concurrency) cumulative /healthz spam on one listener --
    /// matches the reported repro exactly (x20 cumulative survives, x40 cumulative kills it).
    /// </summary>
    [Fact]
    public async Task SequentialHealthzSpam_Cumulative100_ListenerStaysAlive()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 20000);
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 5000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerWindow = 5000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerSubnet = 5000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxSubnetConnectionsPerWindow = 5000;
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().EnableDevOpsEndpoints = true;

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<PlainWsProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new PlainWsProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(300);

        try
        {
            int[] checkpoints = [1, 5, 10, 20, 30, 40, 50, 70, 100];
            int done = 0;
            foreach (int target in checkpoints)
            {
                while (done < target)
                {
                    bool ok = await ProbeHealthzAsync(port);
                    Assert.True(ok, $"/healthz failed at cumulative request #{done + 1}");
                    done++;
                }
            }
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
        catch { /* best-effort cleanup */ }
        InstanceManager.Instance.Clear(dispose: false);
    }
}
