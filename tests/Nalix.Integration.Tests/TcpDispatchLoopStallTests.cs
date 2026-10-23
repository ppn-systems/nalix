// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
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

namespace Nalix.Integration.Tests;

/// <summary>
/// Verifies the dispatch-loop consumer's heartbeat semantics using the actual, unmodified
/// <see cref="Protocol"/> base class (no artificial blocking double) for both TCP and
/// WebSocket. <c>PROCESS_CHANNEL_LOOP_ASYNC</c>'s <c>ctx.Beat()</c> only re-fires once the
/// outer loop re-enters -- gated by draining the fast path or by
/// <c>reader.WaitToReadAsync</c> returning <see langword="true"/> (a new connection arrived).
/// During a genuine idle period with no new connections, the heartbeat legitimately stops
/// advancing; this is expected behavior and must not be conflated with a stalled worker.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class TcpDispatchLoopStallTests : IDisposable
{
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-tcp-stall-{Guid.NewGuid():N}.private");

    public TcpDispatchLoopStallTests()
        => File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");

    private static ushort GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task SendWsHandshakeAsync(Socket socket, string path)
    {
        byte[] keyBytes = new byte[16];
        Random.Shared.NextBytes(keyBytes);
        string key = Convert.ToBase64String(keyBytes);

        string request =
            $"GET {path} HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";

        await socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None);
    }

    /// <summary>
    /// Plain <see cref="Protocol"/> with nothing overridden — no custom <c>OnAccept</c>,
    /// no custom <c>ValidateConnection</c>. Default <c>OnAccept</c> only calls
    /// <c>connection.TCP.BeginReceive(...)</c>, which does not await its receive loop, so it
    /// returns in microseconds.
    /// </summary>
    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class PlainDefaultProtocol : Protocol
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

        public PlainDefaultProtocol() => this.SetConnectionAcceptance(true);

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
        // OnAccept and ValidateConnection are intentionally NOT overridden here.
    }

    /// <summary>
    /// With a fully default protocol (no OnAccept/ValidateConnection override), a long idle
    /// period (no new connections) must be harmless: the dispatch-loop consumer legitimately
    /// suspends inside <c>await reader.WaitToReadAsync(...)</c>, so its heartbeat naturally goes
    /// quiet -- that is NOT a stall. A connection arriving after the idle window must still be
    /// dispatched and processed promptly, proving quiet-heartbeat-during-idle is benign.
    /// </summary>
    [Fact]
    public async Task DefaultProtocol_IdleHeartbeatIsBenign_ConnectionAfterIdleStillProcessed()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 200);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 50);
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 200;

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseConnectionHub(hub);
        builder.MapTcp<PlainDefaultProtocol>()
               .OnPort(port)
               .WithFactory(_ => new PlainDefaultProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(300);

        // 1. Idle window: no connections at all. The dispatch-loop consumer is legitimately
        // suspended in WaitToReadAsync, so ctx.Beat() is not re-invoked -- heartbeat goes quiet
        // by design, not because anything is wrong.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var worker = InstanceManager.Instance
            .GetOrCreateInstance<Nalix.Framework.Tasks.TaskManager>()
            .GetWorkers(runningOnly: true, group: $"net/tcp/{port}")
            .FirstOrDefault(w => w.Name.Contains("dispatch", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(worker);
        DateTimeOffset? heartbeatBeforeConnection = worker!.LastHeartbeatUtc;
        Assert.True(
            heartbeatBeforeConnection is null || (DateTimeOffset.UtcNow - heartbeatBeforeConnection.Value) > TimeSpan.FromSeconds(1),
            "Test setup issue: heartbeat should be quiet after a 2s idle window with no connections.");

        // 2. A connection arrives after the idle window. With a fully default protocol, its
        // OnAccept completes in microseconds -- the consumer must process it and its heartbeat
        // must resume immediately. This is the control case: no stuck OnAccept anywhere.
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);

        bool observed = false;
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(20);
            if (worker.LastHeartbeatUtc is { } t && (DateTimeOffset.UtcNow - t) < TimeSpan.FromMilliseconds(500))
            {
                observed = true;
                break;
            }
        }

        Assert.True(observed,
            "Heartbeat did not resume after a connection arrived post-idle -- with a fully " +
            "default (non-overridden) protocol this proves the dispatch loop itself is healthy; " +
            "a quiet heartbeat during idle is therefore not, by itself, evidence of a stall.");

        await app.DeactivateAsync();
    }

    /// <summary>
    /// Same control case as <see cref="DefaultProtocol_IdleHeartbeatIsBenign_ConnectionAfterIdleStillProcessed"/>
    /// but through the WebSocket path, using a fully default protocol (no OnAccept override).
    /// </summary>
    [Fact]
    public async Task DefaultProtocol_WebSocket_IdleHeartbeatIsBenign_ConnectionAfterIdleStillProcessed()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 200);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 50);
        ConfigurationManager.Instance.Get<ConnectionQuotaOptions>().MaxConnectionsPerIpAddress = 200;

        ConnectionHub hub = new();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<PlainDefaultProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new PlainDefaultProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(300);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var worker = InstanceManager.Instance
            .GetOrCreateInstance<Nalix.Framework.Tasks.TaskManager>()
            .GetWorkers(runningOnly: true, group: $"net/tcp/{port}")
            .FirstOrDefault(w => w.Name.Contains("dispatch", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(worker);

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.ReceiveTimeout = 3000;
        await client.ConnectAsync(IPAddress.Loopback, port);
        await SendWsHandshakeAsync(client, "/ws/");

        byte[] buffer = new byte[256];
        Task<int> readTask = client.ReceiveAsync(buffer, SocketFlags.None);
        Task completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(ReferenceEquals(completed, readTask), "Expected the 101 handshake response to arrive.");
        int read = await readTask;
        Assert.Contains("101", Encoding.ASCII.GetString(buffer, 0, read));

        bool observed = false;
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(20);
            if (worker!.LastHeartbeatUtc is { } t && (DateTimeOffset.UtcNow - t) < TimeSpan.FromMilliseconds(500))
            {
                observed = true;
                break;
            }
        }

        Assert.True(observed,
            "Heartbeat did not resume after a WS connection arrived post-idle with a fully " +
            "default protocol -- the dispatch loop is healthy; idle-quiet heartbeat alone is not " +
            "evidence of a stall.");

        await app.DeactivateAsync();
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
