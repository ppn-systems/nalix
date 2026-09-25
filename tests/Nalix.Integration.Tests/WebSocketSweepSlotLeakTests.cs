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

namespace Nalix.Integration.Tests;

/// <summary>
/// Reproduces the ConnectionGuard per-IP slot leak for stalled WebSocket handshakes that get
/// closed by <c>SWEEP_WS_HANDSHAKE_TIMEOUTS</c> / <c>CloseChainOutsideLock</c>
/// (WebSocketListener.Handle.cs). PR #325 added <c>RELEASE_LIMITER_SLOT</c>/<c>RELEASE_PHYSICAL_SLOT</c>
/// calls only to the DevOps static-response close paths (WebSocketListener.Http.cs). The
/// handshake-timeout sweep path closes sockets via <c>CloseChainOutsideLock</c> without ever
/// calling <c>_limiter.Release(...)</c> for them, even though <c>ProcessAcceptedSocket</c> already
/// consumed a per-IP slot via <c>TryAccept(ip)</c> at accept time. If this is fixed, slots must be
/// released after sweep and a real handshake from the same IP right after must succeed.
/// </summary>
[Collection("NetworkConfigTests")]
public sealed class WebSocketSweepSlotLeakTests : IDisposable
{
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-leak-{Guid.NewGuid():N}.private");

    public WebSocketSweepSlotLeakTests()
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

    private static async Task<Socket> OpenStalledHandshakeAsync(ushort port)
    {
        Socket s = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await s.ConnectAsync(IPAddress.Loopback, port);
        // Truncated request line -- never sends the terminating blank line, then goes silent.
        // The listener still consumes a per-IP ConnectionGuard slot at accept time
        // (ProcessAcceptedSocket -> Limiter.TryAccept(ip)) before this ever times out.
        byte[] partial = Encoding.ASCII.GetBytes("GET /ws/ HTTP/1.1\r\nHost: x\r\n");
        await s.SendAsync(partial, SocketFlags.None);
        return s;
    }

    private static async Task<bool> TryRealHandshakeAsync(ushort port)
    {
        // A rejected-by-limiter connection is closed by the server immediately, which surfaces
        // here as a forcibly-reset SocketException on send/receive rather than a clean response --
        // treat both "no 101" and "reset" as rejection.
        try
        {
            using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.ReceiveTimeout = 3000;
            client.SendTimeout = 3000;
            await client.ConnectAsync(IPAddress.Loopback, port);

            byte[] keyBytes = new byte[16];
            Random.Shared.NextBytes(keyBytes);
            string key = Convert.ToBase64String(keyBytes);
            string request =
                "GET /ws/ HTTP/1.1\r\n" +
                "Host: 127.0.0.1\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";
            await client.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None);

            byte[] buffer = new byte[256];
            Task<int> readTask = client.ReceiveAsync(buffer, SocketFlags.None);
            Task completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(3)));
            if (!ReferenceEquals(completed, readTask))
            {
                return false; // connection accepted but never responded -- also a symptom of exhausted slots
            }

            int read = await readTask;
            return read > 0 && Encoding.ASCII.GetString(buffer, 0, read).Contains("101");
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// A handful of stalled handshakes from loopback consume the (tight) per-IP quota, then get
    /// swept/closed on handshake timeout. A subsequent, fully legitimate handshake from the SAME
    /// IP must succeed once those slots are gone -- if <c>CloseChainOutsideLock</c> never released
    /// them, the per-IP entry stays saturated and the real handshake is wrongly rejected forever.
    /// </summary>
    [Fact]
    public async Task StalledHandshakes_SweptByTimeout_MustReleasePerIpSlot_ForSubsequentRealHandshake()
    {
        ushort port = GetFreePort();

        // Tight enough that leaked slots from the stalled batch are provably exhausting the quota,
        // but generous enough that legitimate concurrent traffic in a healthy build still fits.
        const int maxPerIp = 3;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = maxPerIp;
        // The test clients are on loopback, which is exempt from per-IP quotas by default.
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().ExemptLoopback = false;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerWindow = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerSubnet = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxSubnetConnectionsPerWindow = 2000;
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 20000);
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        int oldHandshakeTimeout = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs;
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs = 500;

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

        List<Socket> stalledClients = new();
        try
        {
            // 1. Saturate the per-IP quota with stalled handshakes -- each consumes a slot via
            // ProcessAcceptedSocket -> Limiter.TryAccept(ip) at accept time.
            for (int i = 0; i < maxPerIp; i++)
            {
                stalledClients.Add(await OpenStalledHandshakeAsync(port));
            }

            // Sanity: quota is saturated right now, so one more attempt from this IP must be
            // rejected by the limiter (proves the slots were actually acquired).
            bool rejectedWhileSaturated = !await TryRealHandshakeAsync(port);
            Assert.True(rejectedWhileSaturated,
                "Test setup issue: per-IP quota should already be saturated by the stalled batch.");

            // 2. Wait past the handshake timeout so SWEEP_WS_HANDSHAKE_TIMEOUTS closes all of them.
            await Task.Delay(1500);

            foreach (Socket s in stalledClients)
            {
                try { s.Close(); } catch { /* already closed by sweep, best-effort */ }
            }

            // 3. The stalled sockets are gone now -- their per-IP slots should have been released
            // by the sweep. A real handshake from the same IP must now succeed.
            bool ok = await TryRealHandshakeAsync(port);

            Assert.True(ok,
                "Real WS handshake was rejected from the same IP after the stalled batch was swept " +
                "by handshake timeout -- this reproduces the ConnectionGuard per-IP slot leak in " +
                "CloseChainOutsideLock (WebSocketListener.Handle.cs): sockets are closed but " +
                "Limiter.Release(ip) is never called for them, unlike the /healthz-close paths " +
                "fixed by PR #325 (WebSocketListener.Http.cs RELEASE_LIMITER_SLOT).");
        }
        finally
        {
            foreach (Socket s in stalledClients)
            {
                try { s.Close(); } catch { /* best-effort cleanup */ }
            }
            ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs = oldHandshakeTimeout;
            ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().ExemptLoopback = true;
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
