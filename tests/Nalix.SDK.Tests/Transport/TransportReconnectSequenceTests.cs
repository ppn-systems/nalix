// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Environment.Sequencing;
using Nalix.Network.Connections;
using Nalix.Network.Listeners.Web;
using Nalix.Network.Options;
using Nalix.Network.Protocols;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport;

[Collection(TestCollections.RealServer)]
public sealed class TransportReconnectSequenceTests : IDisposable
{
    [Fact]
    public async Task ConnectAsync_AfterStaleTcpSocket_ResetsSendSequence()
    {
        int port = TestEnvironment.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            using TcpSession session = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });

            await session.SendAsync(new byte[] { 1 }, encrypt: true);
            Assert.Equal(1u, CurrentSendSequence(session));

            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            await session.ConnectAsync();
            using Socket accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0u, CurrentSendSequence(session));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectAsync_AfterStaleWebSocket_ResetsSendSequence()
    {
        ushort port = (ushort)TestEnvironment.GetFreePort();
        NetworkWebSocketOptions wsOptions = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        string previousHost = wsOptions.Host;
        wsOptions.Host = "127.0.0.1";

        try
        {
            using IConnectionHub hub = new ConnectionHub();
            IConnectionGuard guard = Nalix.Framework.Injection.InstanceManager.Instance.GetOrCreateInstance<Nalix.Network.RateLimiting.ConnectionGuard>();
            using TestWebSocketListener server = new(port, "/ws/", new NoOpProtocol(), hub, guard);
            server.Activate();
            await Task.Delay(500);

            using WebSocketSession session = new(
                new TransportOptions { Address = "127.0.0.1", Port = port },
                new WebSocketTransportOptions { Path = "/ws/" });

            await session.SendAsync(new byte[] { 1 }, encrypt: true);
            Assert.Equal(1u, CurrentSendSequence(session));

            await session.ConnectAsync();

            Assert.Equal(0u, CurrentSendSequence(session));
            server.Deactivate();
        }
        finally
        {
            wsOptions.Host = previousHost;
        }
    }

    [Fact]
    public async Task ConnectAsync_AfterStaleUdpSocket_ResetsSendSequence()
    {
        using UdpSession session = new(new TransportOptions
        {
            Address = "127.0.0.1",
            Port = (ushort)TestEnvironment.GetFreePort()
        });

        await session.SendAsync(new byte[] { 1 }, encrypt: true);
        Assert.Equal(1u, CurrentSendSequence(session));

        await session.ConnectAsync();

        Assert.Equal(0u, CurrentSendSequence(session));
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);

    private static uint CurrentSendSequence(object session)
    {
        object sender = session.GetType().GetField("_sender", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        return ((SequenceCounter)sender.GetType().GetProperty("Sequence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!).Current();
    }

    private sealed class TestWebSocketListener(ushort port, string path, IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
        : WebSocketListenerBase(port, path, protocol, hub, guard);

    [Nalix.Abstractions.Injection.Injectable]
    public sealed class NoOpProtocol : Protocol
    {
        private sealed class NoOpFrameProcessor : IFrameProcessor
        {
            public void ProcessFrame(object? sender, IConnectionEventArgs args) { }
        }

        private sealed class NoOpOpCodeExtractor : Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) => 0;
        }

        public override IFrameProcessor FrameProcessor { get; } = new NoOpFrameProcessor();
        public override Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor OpCodeExtractor { get; } = new NoOpOpCodeExtractor();

        public NoOpProtocol() => this.SetConnectionAcceptance(true);

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }
}
