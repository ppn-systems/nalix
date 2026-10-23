// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Xunit;

namespace Nalix.Integration.Tests;

/// <summary>
/// Reproduces the hypothesized hang: stalled (never-completing) WS handshake sockets
/// sit in the upgrade-context linked list until <c>SWEEP_WS_HANDSHAKE_TIMEOUTS</c> closes
/// them while holding <c>_wsUpgradeLock</c>. If <see cref="Socket.Close"/> blocks on such a
/// socket, every other handshake path (including /ping, /healthz) contends on the same lock
/// and the whole listener appears to hang.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketStalledHandshakeSweepTests : IDisposable
{
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-stall-{Guid.NewGuid():N}.private");

    public WebSocketStalledHandshakeSweepTests()
        => File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");

    private static ushort GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class StallTestProtocol : Protocol
    {
        private sealed class WebTestFrameProcessor : IFrameProcessor
        {
            public void ProcessFrame(object? sender, IConnectionEventArgs args) { }
        }

        private sealed class StubOpCodeExtractor : IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }

        public override IFrameProcessor FrameProcessor { get; } = new WebTestFrameProcessor();
        public override IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public StallTestProtocol() => this.SetConnectionAcceptance(true);

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }

    /// <summary>
    /// N clients connect and send a partial (incomplete) handshake, then go silent forever —
    /// simulating a peer that dies mid-handshake (e.g. dropped by a NAT/tunnel without RST).
    /// Meanwhile we hammer /ping and /healthz. If the sweep's Close() call blocks while
    /// holding _wsUpgradeLock, these requests will stall past the handshake timeout window.
    /// </summary>
    [Fact]
    public async Task StalledHandshakes_DoNotBlockConcurrentHealthzPings()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 50);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 512);
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 200;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerWindow = 200;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerSubnet = 200;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxSubnetConnectionsPerWindow = 200;
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().EnableDevOpsEndpoints = true;

        int oldHandshakeTimeout = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs;
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs = 800;

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<StallTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new StallTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(500);

        List<Socket> stalledClients = new();
        try
        {
            // 1. Open N connections that send a truncated GET line, then go silent (never
            // completing the handshake, never closing). These pile up in the upgrade list.
            const int stalledCount = 30;
            for (int i = 0; i < stalledCount; i++)
            {
                Socket s = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await s.ConnectAsync(IPAddress.Loopback, port);
                byte[] partial = Encoding.ASCII.GetBytes("GET /ws/ HTTP/1.1\r\nHost: x\r\n");
                await s.SendAsync(partial, SocketFlags.None);
                stalledClients.Add(s);
            }

            // 2. Wait past the handshake-timeout + sweep-interval window so the sweeper is
            // actively closing these stalled sockets right about now.
            await Task.Delay(1500);

            // 3. Hammer /healthz and /ping concurrently. Each request opens its own TCP
            // connection (no keep-alive assumptions) and must get a prompt reply.
            const int probeCount = 40;
            var probeTasks = Enumerable.Range(0, probeCount).Select(i => ProbeAsync(port, i % 2 == 0 ? "/healthz" : "/ping"));

            Task allProbes = Task.WhenAll(probeTasks);
            Task completed = await Task.WhenAny(allProbes, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.True(ReferenceEquals(completed, allProbes),
                "Health probes did not all complete within 10s — listener appears stalled, " +
                "consistent with SWEEP_WS_HANDSHAKE_TIMEOUTS blocking on Socket.Close() while holding _wsUpgradeLock.");

            await allProbes; // surface any individual probe exception
        }
        finally
        {
            foreach (Socket s in stalledClients)
            {
                try { s.Close(); } catch { /* best-effort cleanup */ }
            }
            ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs = oldHandshakeTimeout;
            await app.DeactivateAsync();
        }
    }

    private static async Task ProbeAsync(ushort port, string path)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ReceiveTimeout = 5000;
        socket.SendTimeout = 5000;
        await socket.ConnectAsync(IPAddress.Loopback, port);

        byte[] request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        await socket.SendAsync(request, SocketFlags.None);

        byte[] buffer = new byte[256];
        int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
        Assert.True(read > 0, $"No response received for {path}");
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
