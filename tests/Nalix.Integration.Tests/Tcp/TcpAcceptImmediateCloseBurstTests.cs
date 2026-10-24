// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace Nalix.Integration.Tests.Tcp;

/// <summary>
/// Reproduces the reported "immediate_close" scenario: a burst of raw TCP connects that
/// close (RST, via <see cref="LingerOption"/>) immediately after connecting, with no data
/// sent at all -- never reaching the WS handshake path, only exercising the accept-worker
/// (<c>AcceptConnectionsAsync</c> / <c>CreateConnectionAsync</c>) itself. Verifies the
/// accept-worker survives an accumulating burst (tested cumulatively up to 100 on one
/// listener instance, matching the reported repro) and that /healthz stays reachable
/// afterward.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class TcpAcceptImmediateCloseBurstTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-tcp-immclose-{Guid.NewGuid():N}.private");

    public TcpAcceptImmediateCloseBurstTests()
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

    /// <summary>Connects then immediately RST-closes -- no bytes sent, mirrors the reported
    /// Python <c>socket.create_connection(); s.close()</c> repro exactly.</summary>
    private static void ImmediateClose(ushort port)
    {
        using Socket s = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.Connect(IPAddress.Loopback, port);
        s.LingerState = new LingerOption(true, 0);
        s.Close();
    }

    private static async Task<bool> ProbeHealthzAsync(ushort port)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ReceiveTimeout = 5000;
        socket.SendTimeout = 5000;
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port);
        }
        catch (SocketException)
        {
            return false;
        }

        byte[] request = Encoding.ASCII.GetBytes("GET /healthz HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        await socket.SendAsync(request, SocketFlags.None);

        byte[] buffer = new byte[256];
        try
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            return read > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Cumulative immediate-close burst on ONE listener instance (no restart between stages),
    /// matching the reported repro's N=1,5,10,20,30,50,70,100 progression. After each stage,
    /// /healthz must still respond -- if the accept-worker died, every probe after the fatal
    /// stage would get RST (000).
    /// </summary>
    [Fact]
    public async Task Accept_WhenImmediateCloseBurstsAccumulate_KeepsHealthzUp()
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
            int[] stageSizes = [1, 5, 10, 20, 30, 50, 70, 100];
            foreach (int n in stageSizes)
            {
                List<Task> stage = new();
                for (int i = 0; i < n; i++)
                {
                    stage.Add(Task.Run(() => ImmediateClose(port)));
                }
                await Task.WhenAll(stage);

                // Give the accept-worker a moment to drain the burst before probing.
                await Task.Delay(200);

                bool healthy = await ProbeHealthzAsync(port);
                Assert.True(healthy,
                    $"/healthz stopped responding after a cumulative immediate-close burst reached n={n} " +
                    "-- indicates the TCP accept-worker died (regression of the ListenerClosed/SocketAborted " +
                    "unconditional-fatal bug).");
            }
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
