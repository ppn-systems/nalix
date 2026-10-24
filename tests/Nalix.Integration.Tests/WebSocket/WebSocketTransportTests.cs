// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using NSubstitute;
using Nalix.Abstractions.Concurrency;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nalix.Abstractions;
using Nalix.Environment.Memory;
using Nalix.Network.Connections;
using Nalix.Network.Listeners.Web;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.Network.Protocols;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Hosting;
using Nalix.Network.Options;
using Nalix.Framework.Options;
using Nalix.Integration.Tests.TestSupport;

namespace Nalix.Integration.Tests.WebSocket;

[Collection(TestCollections.NetworkConfig)]
public sealed class WebSocketTransportTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ws-test-{Guid.NewGuid():N}.private");

    /// <summary>
    /// A real protocol implementation for testing, inheriting from Nalix.Network.Protocols.Protocol
    /// </summary>
    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class IntegrationTestProtocol : Protocol
    {
        public int ProcessedCount;

        private sealed class WebTestFrameProcessor : IFrameProcessor
        {
            private readonly IntegrationTestProtocol _protocol;
            public WebTestFrameProcessor(IntegrationTestProtocol protocol) => _protocol = protocol;
            public void ProcessFrame(object? sender, IConnectionEventArgs args) => _protocol.ProcessMessage(sender, args);
        }

        private sealed class StubOpCodeExtractor : Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) =>
                payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
        }

        public override IFrameProcessor FrameProcessor { get; }
        public override Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public IntegrationTestProtocol()
        {
            FrameProcessor = new WebTestFrameProcessor(this);
            this.SetConnectionAcceptance(true);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args)
        {
            Interlocked.Increment(ref ProcessedCount);
            // Echo the payload back exactly as received
            if (args.Lease != null && args.Lease.Length > 0)
            {
                args.Connection.TCP.SendAsync(args.Lease.Memory).AsTask().Wait();
            }
        }
    }

    /// <summary>
    /// A concrete implementation of WebSocketListenerBase to expose the server.
    /// </summary>
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

    [Fact]
    public async Task Session_WhenSendingMessage_EchoesMessage()
    {
        // 1. Setup Server using real ConnectionHub and Protocol
        ushort port = GetFreePort();
        
        // Configure Host to 127.0.0.1 so it doesn't require admin privileges on Windows
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        
        var protocol = new IntegrationTestProtocol();
        var hub = new ConnectionHub(); // Real hub
        var guard = Nalix.Framework.Injection.InstanceManager.Instance.GetExistingInstance<IConnectionGuard>() ?? Nalix.Framework.Injection.InstanceManager.Instance.GetOrCreateInstance<Nalix.Network.RateLimiting.ConnectionGuard>();

        using var server = new TestWebSocketListener(port, "/ws/", protocol, hub, guard);
        server.Activate();

        // Wait a little for server to start
        await Task.Delay(1000);

        // 2. Setup Client
        var options = new TransportOptions
        {
            Address = "127.0.0.1",
            Port = port,
            CompressionEnabled = false
        };

        using var client = new WebSocketSession(options);
        
        var messageReceivedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.OnMessageReceived += (s, lease) =>
        {
            if (lease.Length > 10)
            {
                string msg = System.Text.Encoding.UTF8.GetString(lease.Span[10..]);
                messageReceivedTcs.TrySetResult(msg);
            }
        };

        // 3. Connect
        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        // 4. Send with a valid 10-byte packet header prefix
        string testMessage = "Hello WebSocket Nalix!";
        byte[] rawText = System.Text.Encoding.UTF8.GetBytes(testMessage);
        
        byte[] payload = new byte[10 + rawText.Length];
        // OpCode = 0x1234
        payload[4] = 0x34;
        payload[5] = 0x12;
        rawText.CopyTo(payload.AsSpan(10));

        await client.SendAsync(payload);

        // 5. Receive
        var completedTask = await Task.WhenAny(messageReceivedTcs.Task, Task.Delay(5000));
        Assert.Equal(messageReceivedTcs.Task, completedTask); // Should not timeout

        string received = await messageReceivedTcs.Task;
        Assert.Equal(testMessage, received);
        Assert.Equal(1, protocol.ProcessedCount);

        // 6. Cleanup
        await client.DisconnectAsync();
        server.Deactivate();
    }

    [Fact]
    public async Task Hosting_WhenSendingMessage_EchoesMessage()
    {
        // 1. Setup Server using NetworkApplicationBuilder
        ushort port = GetFreePort();
        
        // Configure Host to 127.0.0.1
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        EnsureCertificate();

        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        
        // Bind WebSocket using the fluent API
        builder.ListenWebSocket<IntegrationTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(dispatch => new IntegrationTestProtocol());

        using var app = builder.Build();
        await app.ActivateAsync();

        // Wait a little for server to start
        await Task.Delay(1000);

        try
        {
            // 2. Setup Client
            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = port,
                CompressionEnabled = false
            };

            using var client = new WebSocketSession(options);
            
            var messageReceivedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            client.OnMessageReceived += (s, lease) =>
            {
                if (lease.Length > 10)
                {
                    string msg = System.Text.Encoding.UTF8.GetString(lease.Span[10..]);
                    messageReceivedTcs.TrySetResult(msg);
                }
            };

            // 3. Connect
            await client.ConnectAsync();
            Assert.True(client.IsConnected);

            // 4. Send with a valid 10-byte packet header prefix
            string testMessage = "Hello WebSocket Hosting!";
            byte[] rawText = System.Text.Encoding.UTF8.GetBytes(testMessage);
            
            byte[] payload = new byte[10 + rawText.Length];
            // OpCode = 0x1234
            payload[4] = 0x34;
            payload[5] = 0x12;
            rawText.CopyTo(payload.AsSpan(10));

            await client.SendAsync(payload);

            // 5. Receive
            var completedTask = await Task.WhenAny(messageReceivedTcs.Task, Task.Delay(5000));
            Assert.Equal(messageReceivedTcs.Task, completedTask); // Should not timeout

            string received = await messageReceivedTcs.Task;
            Assert.Equal(testMessage, received);

            // 6. Cleanup
            await client.DisconnectAsync();
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Hosting_WhenClientSendsManyMessages_DoesNotLeakConnectionEventArgs()
    {
        ObjectPoolOptions poolOptions = ConfigurationManager.Instance.Get<ObjectPoolOptions>();
        bool previousDiagnostics = poolOptions.EnableDiagnostics;
        bool previousCaptureStacks = poolOptions.CaptureStackTraces;
        TextWriter originalOut = Console.Out;
        using StringWriter consoleOutput = new();

        poolOptions.EnableDiagnostics = true;
        poolOptions.CaptureStackTraces = true;
        Console.SetOut(consoleOutput);

        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        EnsureCertificate();

        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        var protocol = new IntegrationTestProtocol();
        builder.ListenWebSocket<IntegrationTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => protocol);

        using var app = builder.Build();

        try
        {
            await app.ActivateAsync();
            await Task.Delay(1000);

            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = port,
                CompressionEnabled = false
            };

            using var client = new WebSocketSession(options);
            TaskCompletionSource firstEcho = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnMessageReceived += (_, lease) =>
            {
                if (lease.Length > 10)
                {
                    firstEcho.TrySetResult();
                }
            };

            await client.ConnectAsync();

            for (int i = 0; i < 256; i++)
            {
                await client.SendAsync(CreatePayload($"spam-{i}"));
            }

            Task completed = await Task.WhenAny(firstEcho.Task, Task.Delay(10000));
            Assert.Equal(firstEcho.Task, completed);

            await client.DisconnectAsync();
            await app.DeactivateAsync();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(100);
            }

            Assert.DoesNotContain("LEAK DETECTED: Object of type ConnectionEventArgs", consoleOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            poolOptions.EnableDiagnostics = previousDiagnostics;
            poolOptions.CaptureStackTraces = previousCaptureStacks;
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Hosting_WhenClientSendsMessagesLargerThanReceiveChunk_DoesNotLeakConnectionEventArgs()
    {
        ObjectPoolOptions poolOptions = ConfigurationManager.Instance.Get<ObjectPoolOptions>();
        bool previousDiagnostics = poolOptions.EnableDiagnostics;
        bool previousCaptureStacks = poolOptions.CaptureStackTraces;
        TextWriter originalOut = Console.Out;
        using StringWriter consoleOutput = new();

        poolOptions.EnableDiagnostics = true;
        poolOptions.CaptureStackTraces = true;
        Console.SetOut(consoleOutput);

        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        EnsureCertificate();

        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(_certificatePath);
        builder.ListenWebSocket<IntegrationTestProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(_ => new IntegrationTestProtocol());

        using var app = builder.Build();

        try
        {
            await app.ActivateAsync();
            await Task.Delay(1000);

            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = port,
                CompressionEnabled = false
            };

            using var client = new WebSocketSession(options);
            TaskCompletionSource firstEcho = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnMessageReceived += (_, lease) =>
            {
                if (lease.Length > 1400)
                {
                    firstEcho.TrySetResult();
                }
            };

            await client.ConnectAsync();

            string largeText = new('x', 2_048);
            for (int i = 0; i < 64; i++)
            {
                await client.SendAsync(CreatePayload($"{i:D4}-{largeText}"));
            }

            Task completed = await Task.WhenAny(firstEcho.Task, Task.Delay(10000));
            Assert.Equal(firstEcho.Task, completed);

            await client.DisconnectAsync();
            await app.DeactivateAsync();

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(100);
            }

            Assert.DoesNotContain("LEAK DETECTED: Object of type ConnectionEventArgs", consoleOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            poolOptions.EnableDiagnostics = previousDiagnostics;
            poolOptions.CaptureStackTraces = previousCaptureStacks;
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task Session_WhenInboundMessageExceedsClientLimit_RaisesError()
    {
        ushort port = GetFreePort();
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().MaxMessageSize = 1_048_576;

        var protocol = new IntegrationTestProtocol();
        var hub = new ConnectionHub();
        var guard = Nalix.Framework.Injection.InstanceManager.Instance.GetExistingInstance<IConnectionGuard>() ?? Nalix.Framework.Injection.InstanceManager.Instance.GetOrCreateInstance<Nalix.Network.RateLimiting.ConnectionGuard>();

        using var server = new TestWebSocketListener(port, "/ws/", protocol, hub, guard);
        server.Activate();
        await Task.Delay(1000);

        try
        {
            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = port,
                CompressionEnabled = false
            };

            var webSocketOptions = new WebSocketTransportOptions
            {
                MaxMessageSize = 16
            };

            using var client = new WebSocketSession(options, webSocketOptions);
            TaskCompletionSource<Exception> errorTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnError += (_, ex) => errorTcs.TrySetResult(ex);

            await client.ConnectAsync();
            await client.SendAsync(CreatePayload("this message is larger than sixteen bytes"));

            Task completed = await Task.WhenAny(errorTcs.Task, Task.Delay(5000));
            Assert.Equal(errorTcs.Task, completed);
            Assert.Contains("WebSocket message size", errorTcs.Task.Result.Message, StringComparison.Ordinal);
        }
        finally
        {
            server.Deactivate();
        }
    }

    [Fact]
    public async Task Server_WhenInboundMessageExceedsServerLimit_DisconnectsWithoutEcho()
    {
        ushort port = GetFreePort();
        NetworkWebSocketOptions serverOptions = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        serverOptions.Host = "127.0.0.1";
        serverOptions.MaxMessageSize = 16;

        var protocol = new IntegrationTestProtocol();
        var hub = new ConnectionHub();
        var guard = Nalix.Framework.Injection.InstanceManager.Instance.GetExistingInstance<IConnectionGuard>() ?? Nalix.Framework.Injection.InstanceManager.Instance.GetOrCreateInstance<Nalix.Network.RateLimiting.ConnectionGuard>();

        using var server = new TestWebSocketListener(port, "/ws/", protocol, hub, guard);
        server.Activate();
        await Task.Delay(1000);

        try
        {
            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = port,
                CompressionEnabled = false
            };

            using var client = new WebSocketSession(options);
            TaskCompletionSource<IBufferLease> messageTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<Exception> disconnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            client.OnMessageReceived += (_, lease) => messageTcs.TrySetResult(lease);
            client.OnDisconnected += (_, ex) => disconnectTcs.TrySetResult(ex);

            await client.ConnectAsync();
            await client.SendAsync(CreatePayload("this message is larger than sixteen bytes"));

            Task completed = await Task.WhenAny(disconnectTcs.Task, messageTcs.Task, Task.Delay(5000));
            Assert.NotEqual(messageTcs.Task, completed);
        }
        finally
        {
            serverOptions.MaxMessageSize = 1_048_576;
            server.Deactivate();
        }
    }

    private static byte[] CreatePayload(string text)
    {
        byte[] rawText = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] payload = new byte[10 + rawText.Length];
        payload[4] = 0x34;
        payload[5] = 0x12;
        rawText.CopyTo(payload.AsSpan(10));
        return payload;
    }

    public void Dispose()
    {
        _configSnapshot.Dispose();
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

    private void EnsureCertificate()
        => File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");
}
