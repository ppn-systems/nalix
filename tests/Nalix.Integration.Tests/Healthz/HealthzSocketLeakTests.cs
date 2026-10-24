// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
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
/// Directly measures OS handle count before/after repeated /healthz requests. SEND_STATIC_RESPONSE
/// only Shutdown(Send)s the socket -- it never Close()s it, and ReleaseWsUpgradeContext is called
/// with success:true so its own SafeCloseSocket branch is skipped too. Every /healthz request
/// therefore leaks one socket handle permanently (no tunnel, no TLS, no concurrency needed).
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class HealthzSocketLeakTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-healthz-leak-{Guid.NewGuid():N}.private");

    public HealthzSocketLeakTests()
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
        await socket.ConnectAsync(IPAddress.Loopback, port);
        byte[] request = Encoding.ASCII.GetBytes("GET /healthz HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        await socket.SendAsync(request, SocketFlags.None);

        byte[] buffer = new byte[256];
        int read = await socket.ReceiveAsync(buffer, SocketFlags.None);
        return read > 0;
    }

    [Fact]
    public async Task Healthz_WhenRequestedRepeatedly_DoesNotLeakSocketHandles()
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
            using Process self = Process.GetCurrentProcess();
            self.Refresh();
            int before = self.HandleCount;

            const int requestCount = 100;
            for (int i = 0; i < requestCount; i++)
            {
                Assert.True(await ProbeHealthzAsync(port), $"/healthz failed at request #{i + 1}");
            }

            // Let the OS/runtime settle any legitimately-async teardown before measuring.
            await Task.Delay(500);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            self.Refresh();
            int after = self.HandleCount;

            int leaked = after - before;
            Assert.True(leaked < requestCount / 2,
                $"Handle count grew by {leaked} after {requestCount} /healthz requests (before={before}, after={after}) -- " +
                "server-side sockets for the DevOps endpoints are not being closed.");
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    /// <summary>
    /// DevOps endpoints never released their accept-time limiter slot (no IConnection is
    /// ever created for them), so every /healthz request permanently consumed one slot from
    /// ConnectionQuotaOptions.MaxConnectionsPerSubnet. At the real default (50) the subnet got
    /// banned forever after ~50 requests and every request after that failed -- reproduced live
    /// against a deployed instance. This test uses that same real default to catch a regression.
    /// </summary>
    [Fact]
    public async Task Healthz_WhenRequestedRepeatedly_DoesNotExhaustSubnetQuota()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 20000);
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
            const int requestCount = 150; // > default MaxConnectionsPerSubnet (50)
            for (int i = 0; i < requestCount; i++)
            {
                Assert.True(await ProbeHealthzAsync(port), $"/healthz failed at request #{i + 1} -- subnet quota exhausted?");
            }
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    /// <summary>
    /// DualMode (IPv6+IPv4 dual-stack) sockets surface IPv4 clients as IPv4-mapped-IPv6
    /// addresses (::ffff:a.b.c.d). TRY_ACQUIRE_SUBNET_SLOT keyed off the raw AddressFamily
    /// (InterNetworkV6) while Release() normalizes to plain IPv4 first -- so accept
    /// increments the IPv6 subnet map and release decrements the IPv4 map, permanently
    /// leaking a slot per request. The Host="127.0.0.1" test above doesn't hit dual-stack;
    /// this uses Host="*" (the real production config) to catch that regression.
    /// </summary>
    [Fact]
    public async Task Healthz_WhenRequestedRepeatedlyOnDualStackHost_DoesNotExhaustSubnetQuota()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 20000);
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "*";
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
            const int requestCount = 150; // > default MaxConnectionsPerSubnet (50)
            for (int i = 0; i < requestCount; i++)
            {
                Assert.True(await ProbeHealthzAsync(port), $"/healthz failed at request #{i + 1} -- subnet quota exhausted?");
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
