// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
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
using Nalix.Integration.Tests.TestSupport;

namespace Nalix.Integration.Tests.Healthz;

/// <summary>
/// Reproduces a blocking-Send-without-timeout on the plain HTTP DevOps endpoints
/// (/healthz, /version, /metrics) served by SEND_STATIC_RESPONSE / SEND_METRICS_RESPONSE
/// in WebSocketListener.Http.cs -- distinct from the WS-upgrade-101 response path fixed
/// by commit b63c214f7 (which only touched OnWebSocketReadCompleted in .Handle.cs).
/// A peer that sends the request then abandons the socket (RST) before reading the
/// response forces the server's Socket.Send() to potentially block on a half-open/dead
/// peer if no SendTimeout is set. Cumulative repeats should never hang the listener.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class HealthzSendTimeoutTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-healthz-timeout-{Guid.NewGuid():N}.private");

    public HealthzSendTimeoutTests()
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

    /// <summary>Sends the request, then RSTs immediately without ever reading the response --
    /// mirrors a Cloudflare Tunnel peer that resets the connection right after the request
    /// leaves, before the response arrives.</summary>
    private static void SendThenAbandon(ushort port, string path)
    {
        using Socket s = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.Connect(IPAddress.Loopback, port);
        byte[] request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        s.Send(request, SocketFlags.None);
        s.LingerState = new LingerOption(true, 0);
        s.Close();
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

    [Fact]
    public async Task Healthz_WhenPeersAbandonResponses_DoesNotBlockListener()
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
            string[] paths = ["/healthz", "/version", "/metrics"];
            for (int i = 0; i < 60; i++)
            {
                SendThenAbandon(port, paths[i % paths.Length]);
            }

            // If Send() blocked on any abandoned peer, this probe (and the listener as a
            // whole) would hang past its own timeout.
            Task<bool> probe = ProbeHealthzAsync(port);
            Task completed = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(completed, probe),
                "/healthz did not respond within 10s after a burst of abandoned peers -- " +
                "indicates SEND_STATIC_RESPONSE/SEND_METRICS_RESPONSE blocked on Socket.Send() " +
                "without a timeout for a dead peer.");

            bool ok = await probe;
            Assert.True(ok, "/healthz probe failed after abandoned-peer burst.");
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
        catch { /* best-effort cleanup */ }
        InstanceManager.Instance.Clear(dispose: false);
    }
}
