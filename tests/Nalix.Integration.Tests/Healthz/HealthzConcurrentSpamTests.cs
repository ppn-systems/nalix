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

namespace Nalix.Integration.Tests.Healthz;

/// <summary>
/// Many concurrent threads/connections hammering /healthz at once on one listener --
/// distinct from the sequential-cumulative repro: here N callers hit the same listener
/// AT THE SAME TIME, matching a real burst of health-check traffic instead of one-at-a-time
/// probing. Listener must stay up and answer every request.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class HealthzConcurrentSpamTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-healthz-concurrent-{Guid.NewGuid():N}.private");

    public HealthzConcurrentSpamTests()
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
        socket.ReceiveTimeout = 5000;
        socket.SendTimeout = 5000;
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
    /// N threads fire /healthz at the same instant, repeated in waves, on the SAME listener
    /// instance (no restart). The listener must survive every wave and keep answering
    /// afterward -- if concurrent access to shared state (upgrade list / lock / socket pool)
    /// has a race, this is where it would show up.
    /// </summary>
    [Fact]
    public async Task Healthz_WhenConcurrentBurstsHitListener_ListenerStaysAlive()
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
            int[] waveSizes = [1, 5, 10, 20, 30, 50, 100];
            foreach (int n in waveSizes)
            {
                IEnumerable<Task<bool>> wave = Enumerable.Range(0, n).Select(_ => ProbeHealthzAsync(port));
                Task<bool[]> all = Task.WhenAll(wave);
                Task completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15)));

                Assert.True(ReferenceEquals(completed, all),
                    $"Concurrent /healthz wave of size {n} did not complete within 15s -- listener appears hung.");

                bool[] results = await all;
                Assert.True(results.All(ok => ok), $"Some /healthz probes failed in a concurrent wave of size {n}.");
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
