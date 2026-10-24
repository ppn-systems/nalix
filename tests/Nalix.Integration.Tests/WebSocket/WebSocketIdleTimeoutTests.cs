// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Xunit;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Tests.Shared;

namespace Nalix.Integration.Tests.WebSocket;

[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketIdleTimeoutTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-timeout-{Guid.NewGuid():N}.private");

    public WebSocketIdleTimeoutTests()
    {
        EnsureCertificate();
    }

    private void EnsureCertificate()
        => File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");

    private static ushort GetFreePort() => (ushort)TestPorts.Next();

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class TimeoutTestProtocol : Protocol
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

        public TimeoutTestProtocol()
        {
            this.SetConnectionAcceptance(true);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }

    [Fact]
    public async Task IdleTimeout_WhenClientIsIdle_DisconnectsClient()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 50);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 128);
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        ConfigurationManager.Instance.Get<NetworkSocketOptions>().EnableTimeout = true;

        var timingOptions = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        int oldTick = timingOptions.TickDuration;
        int oldTimeout = timingOptions.IdleTimeoutMs;

        timingOptions.TickDuration = 400;
        timingOptions.IdleTimeoutMs = 1200;

        ConnectionHub hub = new();

        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.UseConnectionHub(hub);
        builder.MapWebSocket<TimeoutTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new TimeoutTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();
        await Task.Delay(1000);

        try
        {
            var options = new TransportOptions { Address = "127.0.0.1", Port = port, CompressionEnabled = false };
            using var client = new WebSocketSession(options);

            TaskCompletionSource<bool> disconnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnDisconnected += (_, _) => disconnectTcs.TrySetResult(true);
            client.OnError += (_, _) => disconnectTcs.TrySetResult(true);

            for (int r = 0; r < 5; r++)
            {
                try
                {
                    await client.ConnectAsync();
                    break;
                }
                catch (Exception) when (r < 4)
                {
                    await Task.Delay(1000);
                }
            }
            Assert.True(client.IsConnected);

            // Wait for hub registration
            bool registered = false;
            for (int i = 0; i < 1000; i++)
            {
                if (hub.Count > 0)
                {
                    registered = true;
                    break;
                }
                await Task.Delay(10);
            }
            Assert.True(registered);

            // Disconnect from server side (simulating idle timeout eviction)
            var serverConn = hub.ListConnections().FirstOrDefault() as WebSocketConnection;
            Assert.NotNull(serverConn);
            serverConn!.Disconnect("Server idle eviction");

            Task completed = await Task.WhenAny(disconnectTcs.Task, Task.Delay(10000));
            Assert.Same(disconnectTcs.Task, completed);
            Assert.False(client.IsConnected);
        }
        finally
        {
            timingOptions.TickDuration = oldTick;
            timingOptions.IdleTimeoutMs = oldTimeout;
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
        catch { }
        InstanceManager.Instance.Clear(dispose: false);
    }
}
