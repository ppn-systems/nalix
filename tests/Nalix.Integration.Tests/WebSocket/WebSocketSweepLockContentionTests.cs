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

namespace Nalix.Integration.Tests.WebSocket;

/// <summary>
/// Real (non-mocked) reproduction of the <c>_wsUpgradeLock</c> contention hang: a burst of
/// stalled handshake sockets pile up in <c>WebSocketListenerBase</c>'s intrusive upgrade list
/// and get closed by <c>SWEEP_WS_HANDSHAKE_TIMEOUTS</c> while genuine concurrent WS clients are
/// completing real handshakes against the SAME lock. Uses the fully default <see cref="Protocol"/>
/// (no OnAccept override) so any hang here can only come from the listener's own lock/close
/// logic, not from a slow user handler.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketSweepLockContentionTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-sweep-{Guid.NewGuid():N}.private");

    public WebSocketSweepLockContentionTests()
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
        // Truncated request line -- never sends the blank line that terminates HTTP headers,
        // then goes silent forever. The listener's read loop stays pending until the sweep's
        // handshake timeout fires and closes it.
        byte[] partial = Encoding.ASCII.GetBytes("GET /ws/ HTTP/1.1\r\nHost: x\r\n");
        await s.SendAsync(partial, SocketFlags.None);
        return s;
    }

    private static async Task<bool> DoRealHandshakeAsync(ushort port)
    {
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
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
        Task completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(4)));
        if (!ReferenceEquals(completed, readTask))
        {
            return false;
        }

        int read = await readTask;
        return read > 0 && Encoding.ASCII.GetString(buffer, 0, read).Contains("101");
    }

    /// <summary>
    /// A large burst of stalled handshakes piles up, times out, and gets swept concurrently
    /// while genuine WS clients keep handshaking. If the sweep closed sockets while holding
    /// <c>_wsUpgradeLock</c>, real handshakes queued on the same lock would stall past their
    /// own read timeout. All real handshakes must complete with a 101 response.
    /// </summary>
    [Fact]
    public async Task Handshake_WithConcurrentStalledBurst_IsNotDelayed()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 20000);
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerWindow = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerSubnet = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxSubnetConnectionsPerWindow = 2000;
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
            // 1. Open many stalled connections up front -- they'll all expire around the same
            // sweep tick, forcing the sweep to close a large batch in one pass.
            const int stalledCount = 20;
            for (int i = 0; i < stalledCount; i++)
            {
                stalledClients.Add(await OpenStalledHandshakeAsync(port));
            }

            // 2. Wait until just past the handshake timeout so the sweep is actively closing
            // the whole batch, then immediately hammer real handshakes concurrently with it.
            await Task.Delay(600);

            const int realCount = 8;
            IEnumerable<Task<bool>> realHandshakes = Enumerable.Range(0, realCount).Select(_ => DoRealHandshakeAsync(port));
            bool[] results = await Task.WhenAll(realHandshakes);

            Assert.All(results, ok => Assert.True(ok,
                "A real WS handshake failed to receive a 101 response while a stalled-handshake " +
                "sweep was running concurrently -- indicates _wsUpgradeLock contention/regression."));
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

    /// <summary>
    /// Repeats stalled-burst + sweep cycles back to back, then verifies a plain handshake still
    /// completes promptly afterward -- guards against list-corruption bugs in the zero-alloc
    /// chain detach (e.g. reusing <c>.Next</c> as scratch space leaking a stale link back into
    /// the live upgrade list).
    /// </summary>
    [Fact]
    public async Task Sweep_WhenCyclesRepeat_LeavesUpgradeListConsistent()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 20000);
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerWindow = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerSubnet = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxSubnetConnectionsPerWindow = 2000;
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

        try
        {
            for (int round = 0; round < 4; round++)
            {
                List<Socket> stalled = new();
                for (int i = 0; i < 15; i++)
                {
                    stalled.Add(await OpenStalledHandshakeAsync(port));
                }

                await Task.Delay(500); // let this round's sweep fire and close them

                foreach (Socket s in stalled)
                {
                    try { s.Close(); } catch { /* already closed by sweep, best-effort */ }
                }
            }

            bool ok = await DoRealHandshakeAsync(port);
            Assert.True(ok, "Plain handshake failed after repeated sweep cycles -- upgrade list likely corrupted by the zero-alloc chain detach.");
        }
        finally
        {
            ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs = oldHandshakeTimeout;
            await app.DeactivateAsync();
        }
    }

    /// <summary>
    /// Peers that RST mid-handshake (abrupt close, not a silent stall) must be detached via the
    /// error path in <c>OnWebSocketReadCompleted</c> exactly once, concurrently with the sweep
    /// racing to detach OTHER, genuinely-stalled peers on the same tick. Exercises the race
    /// between <c>DetachWsUpgradeContext</c>'s idempotency guard (<c>RemovedFromList</c>) called
    /// from two different code paths (read-completion error vs. sweep timeout) instead of only
    /// the sweep path in isolation.
    /// </summary>
    [Fact]
    public async Task Sweep_WhenAbruptResetPeersRace_DoesNotCorruptUpgradeList()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 20000);
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerWindow = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerSubnet = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxSubnetConnectionsPerWindow = 2000;
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

        try
        {
            // Half the peers stall silently (swept by timeout); half RST immediately after the
            // partial send (detected by OnWebSocketReadCompleted's SocketError branch). Both
            // detach paths touch the same intrusive list under _wsUpgradeLock concurrently.
            List<Socket> stalled = new();
            for (int i = 0; i < 15; i++)
            {
                stalled.Add(await OpenStalledHandshakeAsync(port));
            }

            for (int i = 0; i < 15; i++)
            {
                Socket s = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await s.ConnectAsync(IPAddress.Loopback, port);
                byte[] partial = Encoding.ASCII.GetBytes("GET /ws/ HTTP/1.1\r\nHost: x\r\n");
                await s.SendAsync(partial, SocketFlags.None);
                // Hard reset: LingerState with enabled=true, timeout=0 forces RST on close
                // instead of a graceful FIN, so the server sees an immediate read error.
                s.LingerState = new LingerOption(true, 0);
                s.Close();
            }

            await Task.Delay(700); // let the sweep run against the still-stalled half

            foreach (Socket s in stalled)
            {
                try { s.Close(); } catch { /* best-effort cleanup */ }
            }

            bool ok = await DoRealHandshakeAsync(port);
            Assert.True(ok, "Plain handshake failed after mixed RST/stall detach race -- upgrade list likely corrupted.");
        }
        finally
        {
            ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs = oldHandshakeTimeout;
            await app.DeactivateAsync();
        }
    }

    /// <summary>
    /// Shuts the listener down (Deactivate -&gt; CLEANUP_WS_UPGRADES) while a batch of stalled
    /// handshakes is still pending in the upgrade list, i.e. before the periodic sweep has had a
    /// chance to time them out. <c>CLEANUP_WS_UPGRADES</c> shares the same zero-alloc chain-detach
    /// code path as the sweep; this must complete promptly without throwing and without hanging,
    /// even though every pending socket is still "young" (not yet timed out).
    /// </summary>
    [Fact]
    public async Task Deactivate_WithPendingStalledHandshakes_CompletesPromptly()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 20000);
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerWindow = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerSubnet = 2000;
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxSubnetConnectionsPerWindow = 2000;
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        int oldHandshakeTimeout = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs;
        // Long timeout -- these sockets are guaranteed still "pending" (not swept) at shutdown.
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs = 30000;

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
            const int stalledCount = 20;
            for (int i = 0; i < stalledCount; i++)
            {
                stalledClients.Add(await OpenStalledHandshakeAsync(port));
            }

            await Task.Delay(200); // ensure all are enqueued in the upgrade list

            Task deactivate = app.DeactivateAsync();
            Task completed = await Task.WhenAny(deactivate, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.True(ReferenceEquals(completed, deactivate),
                "DeactivateAsync did not complete within 10s while stalled handshakes were still pending -- " +
                "CLEANUP_WS_UPGRADES likely deadlocked or is blocking on Socket.Close() while holding _wsUpgradeLock.");

            await deactivate; // surface any exception
        }
        finally
        {
            foreach (Socket s in stalledClients)
            {
                try { s.Close(); } catch { /* best-effort cleanup */ }
            }
            ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().HandshakeTimeoutMs = oldHandshakeTimeout;
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
