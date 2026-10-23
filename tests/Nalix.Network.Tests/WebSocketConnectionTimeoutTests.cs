using NSubstitute;
using Nalix.Abstractions.Concurrency;
// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Network.Connections;
using Nalix.Network.Listeners.Web;
using Nalix.Network.Internal.Time;
using Nalix.Network.Options;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.Network.Protocols;
using FluentAssertions;

namespace Nalix.Network.Tests;

[Collection("NetworkConfigTests")]
public class WebSocketConnectionTimeoutTests : IDisposable
{
    public WebSocketConnectionTimeoutTests()
    {
    }
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-test-{Guid.NewGuid():N}.private");

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class IntegrationTestProtocol : Protocol
    {
        private sealed class NoOpFrameProcessor : IFrameProcessor
        {
            public void ProcessFrame(object? sender, IConnectionEventArgs args) { }
        }

        private sealed class StubOpCodeExtractor : Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) =>
                payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
        }

        public override IFrameProcessor FrameProcessor { get; } = new NoOpFrameProcessor();
        public override Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public IntegrationTestProtocol()
        {
            this.SetConnectionAcceptance(true);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args)
        {
            if (args.Lease != null && args.Lease.Length > 0)
            {
                args.Connection.TCP.SendAsync(args.Lease.Memory).AsTask().Wait();
            }
        }
    }

    private sealed class TestWebSocketListener : WebSocketListenerBase
    {
        public TestWebSocketListener(ushort port, string path, IProtocol protocol, IConnectionHub hub, IConnectionGuard guard) : base(port, path, protocol, hub, guard) { }
    }

    private static ushort GetFreePort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static TestWebSocketListener StartTestServerRobustly(IProtocol protocol, IConnectionHub hub, out ushort port)
    {
        string lastReport = "";
        var guard = Nalix.Framework.Injection.InstanceManager.Instance.GetExistingInstance<IConnectionGuard>() ?? Nalix.Framework.Injection.InstanceManager.Instance.GetOrCreateInstance<Nalix.Network.RateLimiting.ConnectionGuard>();
        for (int i = 0; i < 10; i++)
        {
            port = GetFreePort();
            var server = new TestWebSocketListener(port, "/ws/", protocol, hub, guard);
            server.Activate();
            System.Threading.Thread.Sleep(500); // Give it a moment to fail if port is busy
            lastReport = server.GenerateReport();
            if (lastReport.Contains("RUNNING"))
            {
                return server;
            }
            server.Deactivate();
            server.Dispose();
        }
        throw new Exception($"Could not start test server on any free port after 10 attempts. Last report: {lastReport}");
    }

    [Fact]
    public async Task WebSocketSession_DisconnectBeforeTimeout_RemovesTimeoutTask()
    {
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 50);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 128);
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        ConfigurationManager.Instance.Get<NetworkSocketOptions>().EnableTimeout = true;

        var protocol = new IntegrationTestProtocol();
        var hub = new ConnectionHub();

        using var server = StartTestServerRobustly(protocol, hub, out ushort port);
        await Task.Delay(3500); // Remaining delay for slow CI

        try
        {
            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = port,
                CompressionEnabled = false
            };

            using var client = new WebSocketSession(options);
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
            client.IsConnected.Should().BeTrue();

            // Wait for the connection to be fully registered in the hub and initialized (up to 10 seconds)
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
            registered.Should().BeTrue("Connection should be registered in the hub within 10 seconds.");

            // Verify a connection is registered in the hub and has a TimeoutTask
            var connections = hub.ListConnections().ToArray();
            connections.Should().ContainSingle();
            var conn = connections[0];
            
            conn.Should().BeOfType<WebSocketConnection>();
            var wsConn = (WebSocketConnection)conn;
            
            var timeoutTracked = (TimingWheel.ITimeoutTrackedConnection)wsConn;
            
            timeoutTracked.IsRegisteredInWheel.Should().BeTrue();
            
            var taskReference = timeoutTracked.TimeoutTask;
            taskReference.Should().NotBeNull();
            taskReference!.Bucket.Should().NotBeNull(); // It must be registered inside a bucket

            // Now disconnect client
            await client.DisconnectAsync();
            
            // Wait for disconnect event to propagate and Unregister to be called (up to 10 seconds)
            bool unregistered = false;
            for (int i = 0; i < 1000; i++)
            {
                if (!timeoutTracked.IsRegisteredInWheel)
                {
                    unregistered = true;
                    break;
                }
                await Task.Delay(10);
            }
            unregistered.Should().BeTrue("Connection should be unregistered from the wheel.");

            // Check that it's no longer registered in wheel, and TimeoutTask was immediately removed from bucket!
            timeoutTracked.IsRegisteredInWheel.Should().BeFalse();
            timeoutTracked.TimeoutTask.Should().BeNull();
            taskReference.Bucket.Should().BeNull(); // The task was successfully removed from the bucket!
            taskReference.Conn.Should().BeNull(); // The task was returned to the pool (Conn was nullified)!
        }
        finally
        {
            server.Deactivate();
        }
    }

    [Fact(Skip = "Flaky under full Network.Tests run: shared TimingWheel/TaskManager state prevents deterministic idle tick.")]
    public async Task WebSocketSession_IdleTimeout_DisconnectsClient()
    {
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxConnections", 2000);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxErrorThreshold", 50);
        ConfigurationManager.Instance.UpdateValue<ConnectionGuardOptions>("MaxPacketPerSecond", 128);
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        ConfigurationManager.Instance.Get<NetworkSocketOptions>().EnableTimeout = true;

        // Configure dynamic short timeout for TimingWheel (generous enough for slow CI runner CPUs)
        var timingOptions = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        int oldTick = timingOptions.TickDuration;
        int oldTimeout = timingOptions.IdleTimeoutMs;
        
        timingOptions.TickDuration = 400; // 400ms ticks
        timingOptions.IdleTimeoutMs = 1200; // 1200ms idle timeout (only needs 3 ticks to trigger)

        try
        {
            var protocol = new IntegrationTestProtocol();
            var hub = new ConnectionHub();

            using var server = StartTestServerRobustly(protocol, hub, out ushort port);
            await Task.Delay(3500); // Remaining delay for slow CI

            try
            {
                var options = new TransportOptions
                {
                    Address = "127.0.0.1",
                    Port = port,
                    CompressionEnabled = false
                };

                using var client = new WebSocketSession(options);
                
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
                client.IsConnected.Should().BeTrue();

                // Wait for the connection to be fully registered in the hub and initialized (up to 10 seconds)
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
                registered.Should().BeTrue("Connection should be registered in the hub within 10 seconds.");

                var serverConn = hub.ListConnections().FirstOrDefault() as WebSocketConnection;
                if (serverConn != null)
                {
                    // Force the idle time to be huge so it times out regardless of shared TimingWheelOptions state
                    serverConn.LastPingTime = 0;
                    // Also force the timeout to be tiny so it gets re-scheduled into the very next bucket!
                    serverConn.UpdateIdleTimeout(1);
                }

                // Wait for the TimingWheel loop to tick and reap the server-side connection.
                bool disconnected = false;
                for (int i = 0; i < 3000; i++)
                {
                    if (hub.Count == 0 || serverConn?.IsDisposed == true)
                    {
                        disconnected = true;
                        break;
                    }
                    await Task.Delay(10);
                }
                disconnected.Should().BeTrue("TimingWheel should reap the idle WebSocket connection.");
            }
            finally
            {
                server.Deactivate();
                timingOptions.TickDuration = oldTick;
                timingOptions.IdleTimeoutMs = oldTimeout;
            }
        }
        finally
        {
            // Restore TimingWheel options
            timingOptions.TickDuration = oldTick;
            timingOptions.IdleTimeoutMs = oldTimeout;
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_certificatePath))
            {
                File.Delete(_certificatePath);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }

        Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
    }
}
