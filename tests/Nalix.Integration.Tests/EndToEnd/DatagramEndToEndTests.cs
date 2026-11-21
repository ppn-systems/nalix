// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Network.Connections;
using Nalix.Network.Listeners.Datagram;
using Nalix.Network.Options;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Tests.Shared;
using Xunit;

namespace Nalix.Integration.Tests.EndToEnd;

/// <summary>
/// The Nalix Datagram Protocol end to end: a ticket over an encrypted TCP session, then requests over UDP alone.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class DatagramEndToEndTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly RequestOptions s_request = RequestOptions.Default.WithTimeout(5_000);

    private readonly NetworkConfigSnapshot _configSnapshot = new();
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ndp-{Guid.NewGuid():N}.private");

    public DatagramEndToEndTests()
    {
        File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    public void Dispose()
    {
        _configSnapshot.Dispose();
        File.Delete(_certificatePath);
        InstanceManager.Instance.Clear(dispose: false);
    }

    [Fact]
    public async Task RequestAsync_OverDatagramSession_ReturnsHandlerResponseEncryptedOnWire()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        Assert.True(datagram.IsConnected);
        Assert.Equal(udpPort, datagram.Ticket!.Value.Port);

        EchoPacket response = await datagram.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 1, Text = "over udp" }, s_request).AsTask().WaitAsync(s_timeout);

        Assert.Equal(2, response.Sequence);
        Assert.Equal("OVER UDP", response.Text);

        // The session AEAD protects the frame, so handlers see it as encrypted on the wire.
        Assert.True(EchoHandlers.LastEchoEncryptedOnWire);
    }

    [Fact]
    public async Task DatagramSession_AfterTheStreamIsClosed_KeepsWorking()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);

        TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        await stream.DisconnectAsync().WaitAsync(s_timeout);
        stream.Dispose();

        for (int i = 0; i < 20; i++)
        {
            EchoPacket response = await datagram.RequestAsync<EchoPacket>(
                new EchoPacket { Sequence = i, Text = $"m{i}" }, s_request).AsTask().WaitAsync(s_timeout);
            Assert.Equal(i + 1, response.Sequence);
        }
    }

    [Fact]
    public async Task RequestAsync_WithMessageLargerThanADatagram_IsFragmentedBothWays()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        Random random = new(99);
        string text = new([.. Enumerable.Range(0, 60_000).Select(_ => (char)random.Next('a', 'z' + 1))]);

        EchoPacket response = await datagram.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 3, Text = text }, s_request).AsTask().WaitAsync(s_timeout);

        Assert.Equal(text.ToUpperInvariant(), response.Text);
    }

    [Fact]
    public async Task ConnectAsync_WithoutCompletedHandshake_IsRefused()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);

        using TcpSession stream = new(CreateOptions(tcpPort));
        await stream.ConnectAsync().WaitAsync(s_timeout);

        using DatagramSession datagram = new(stream);
        NetworkException error = await Assert.ThrowsAsync<NetworkException>(() => datagram.ConnectAsync().WaitAsync(s_timeout));
        Assert.Contains("refused", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisconnectAsync_ClosesTheServerSession()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);
        DatagramListener listener = (DatagramListener)InstanceManager.Instance.GetExistingInstance<IDatagramTicketIssuer>()!;

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        await datagram.ConnectAsync().WaitAsync(s_timeout);
        Assert.Equal(1, listener.SessionCount);

        await datagram.DisconnectAsync().WaitAsync(s_timeout);

        await WaitUntilAsync(() => listener.SessionCount == 0);
        Assert.False(datagram.IsConnected);
    }

    [Fact]
    public async Task Listener_DropsForeignDatagramsSilently_AndCountsAcceptedSessions()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);
        DatagramListener listener = (DatagramListener)InstanceManager.Instance.GetExistingInstance<IDatagramTicketIssuer>()!;

        using (System.Net.Sockets.UdpClient raw = new())
        {
            // Too short to carry an NDP header, then a well-formed header with an unknown connection identifier.
            _ = await raw.SendAsync(new byte[] { 0x01, 0x02 }, Host, udpPort);
            byte[] unknown = new byte[64];
            unknown[0] = 0x40;
            unknown[1] = 0xEE;
            _ = await raw.SendAsync(unknown, Host, udpPort);
        }

        await WaitUntilAsync(() => listener.Metrics.DatagramsReceived >= 2);
        Assert.Equal(0, listener.SessionCount);
        Assert.Equal(2, listener.Metrics.TotalDropped);

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        Assert.Equal(1, listener.Metrics.TicketsIssued);
        Assert.Equal(1, listener.Metrics.TotalAccepted);
        Assert.Equal(0, listener.PendingTicketCount);
        Assert.Contains("Established         : 1", listener.GenerateReport(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerDisconnect_WhileAnotherThreadIsDraining_StillSendsClose()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);
        IConnectionHub hub = InstanceManager.Instance.GetExistingInstance<IConnectionHub>()!;

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        datagram.OnDisconnected += (_, _) => disconnected.TrySetResult();
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        DatagramConnection server = hub.ListConnections().OfType<DatagramConnection>().Single();

        // Hold the session's send lock, as a thread in the middle of a drain does, while Disconnect runs elsewhere.
        object socket = typeof(DatagramConnection).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        Lock sendLock = (Lock)socket.GetType().GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(socket)!;

        sendLock.Enter();
        try
        {
            Assert.True(Task.Run(() => server.Disconnect("test")).Wait(s_timeout), "Disconnect must not wait for the drain.");
        }
        finally
        {
            sendLock.Exit();
        }

        // Without the CLOSE frame the client only notices after the 15 s idle timeout.
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(datagram.IsConnected);
    }

    [Fact]
    public async Task ConnectionGuard_SlotsOfDatagramSessions_AreReleasedOnEveryClosePath()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        CountingGuard guard = new();
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort, guard);
        IConnectionHub hub = InstanceManager.Instance.GetExistingInstance<IConnectionHub>()!;
        DatagramListener listener = (DatagramListener)InstanceManager.Instance.GetExistingInstance<IDatagramTicketIssuer>()!;

        using (TcpSession stream = await ConnectStreamAsync(tcpPort))
        {
            // Closed by the client.
            using (DatagramSession datagram = new(stream))
            {
                await datagram.ConnectAsync().WaitAsync(s_timeout);
                await datagram.DisconnectAsync().WaitAsync(s_timeout);
            }

            await WaitUntilAsync(() => listener.SessionCount == 0);

            // Closed by the server.
            using (DatagramSession datagram = new(stream))
            {
                await datagram.ConnectAsync().WaitAsync(s_timeout);
                hub.ListConnections().OfType<DatagramConnection>().Single().Disconnect("test");
            }

            await WaitUntilAsync(() => listener.SessionCount == 0);
            Assert.Equal(2, listener.Metrics.TotalAccepted);
        }

        // One slot for the stream connection and one per datagram session, each released once.
        await WaitUntilAsync(() => guard.Released == guard.Accepted);
        Assert.Equal(3, guard.Accepted);
    }

    [Fact]
    public async Task ServerDatagramConnection_ExposesItsStateAndSendsThroughDeliveryViews()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);
        IConnectionHub hub = InstanceManager.Instance.GetExistingInstance<IConnectionHub>()!;

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        TaskCompletionSource<int> pushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        datagram.OnMessageReceived += (_, lease) => pushed.TrySetResult(lease.Length);
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        DatagramConnection server = hub.ListConnections().OfType<DatagramConnection>().Single();

        Assert.Contains("DatagramConnection", server.ToString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(server.EngineReport));
        Assert.NotNull(server.Attributes);
        Assert.NotNull(server.RateLimitCache);
        Assert.True(server.BytesReceived > 0);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => server.UpdateIdleTimeout(0));

        // The default delivery is the connection itself; other views are cached per method and channel.
        Assert.Same(server, server.WithDelivery(DeliveryMethod.ReliableOrdered, 0));
        IConnection.ITransport view = server.WithDelivery(DeliveryMethod.ReliableUnordered, 2);
        Assert.Same(view, server.WithDelivery(DeliveryMethod.ReliableUnordered, 2));

        byte[] frame = new EchoPacket { Sequence = 5, Text = "view" }.Serialize();
        await view.SendAsync(frame);
        Assert.Equal(frame.Length, await pushed.Task.WaitAsync(s_timeout));
        Assert.True(server.BytesSent > 0);
    }

    [Fact]
    public async Task ServerDatagramConnection_PastTheErrorThreshold_DisconnectsTheClient()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);
        IConnectionHub hub = InstanceManager.Instance.GetExistingInstance<IConnectionHub>()!;
        DatagramListener listener = (DatagramListener)InstanceManager.Instance.GetExistingInstance<IDatagramTicketIssuer>()!;

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        datagram.OnDisconnected += (_, _) => disconnected.TrySetResult();
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        DatagramConnection server = hub.ListConnections().OfType<DatagramConnection>().Single();
        // The options instance the connection reads (other tests raise the shared threshold, e.g. to 2000).
        var limits = (ConnectionGuardOptions)typeof(DatagramConnection)
            .GetField("s_limitOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int threshold = limits.MaxErrorThreshold;
        int errors = 0;
        while (!server.IsDisposed && errors < threshold * 2)
        {
            server.IncrementErrorCount();
            errors++;
        }

        // Disconnect disposes the session before it returns, like the stream connections.
        Assert.True(server.IsDisposed);
        Assert.Equal(threshold, errors);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => listener.SessionCount == 0);
    }

    [Fact]
    public async Task ConnectAsync_WhenTheGuardRefusesTheSession_FailsAndCountsTheRejection()
    {
        ConfigurationManager.Instance.Get<DatagramOptions>().TicketLifetimeSeconds = 1;
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        CountingGuard guard = new(acceptFirst: 1); // the stream connection only
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort, guard);
        DatagramListener listener = (DatagramListener)InstanceManager.Instance.GetExistingInstance<IDatagramTicketIssuer>()!;

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);

        _ = await Assert.ThrowsAsync<NetworkException>(() => datagram.ConnectAsync().WaitAsync(s_timeout));

        Assert.Equal(1, listener.Metrics.TotalGuardRejections);
        Assert.Equal(0, listener.SessionCount);
        Assert.Equal(0, listener.Metrics.TotalAccepted);
    }

    /// <summary>Counts slots taken and released; admits the first <c>acceptFirst</c> requests, or all of them.</summary>
    private sealed class CountingGuard(int acceptFirst = int.MaxValue) : IConnectionGuard
    {
        private int _accepted;
        private int _released;
        private int _requests;

        public int Accepted => Volatile.Read(ref _accepted);

        public int Released => Volatile.Read(ref _released);

        public bool TryAccept(System.Net.IPEndPoint endPoint)
        {
            if (Interlocked.Increment(ref _requests) > acceptFirst)
            {
                return false;
            }

            _ = Interlocked.Increment(ref _accepted);
            return true;
        }

        public void Release(System.Net.IPEndPoint endPoint) => Interlocked.Increment(ref _released);

        public void OnConnectionClosed(object? sender, IConnectionEventArgs args) => Interlocked.Increment(ref _released);

        public void BanEndpoint(System.Net.IPAddress address, TimeSpan duration)
        {
        }

        public bool IsTrustedProxy(System.Net.IPEndPoint? endPoint) => false;

        public string GenerateReport() => string.Empty;

        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
        {
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ServerDatagramConnection_ExposesItsStreamAndValidatesDeliveryViews()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);
        IConnectionHub hub = InstanceManager.Instance.GetExistingInstance<IConnectionHub>()!;

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        DatagramConnection server = hub.ListConnections().OfType<DatagramConnection>().Single();
        IConnection serverStream = hub.ListConnections().Single(c => c is not DatagramConnection);

        // The link a kick needs to close the datagram session with its stream connection.
        Assert.Equal(serverStream.ConnectionId, server.StreamConnectionId);

        byte channels = datagram.Ticket!.Value.ChannelCount;
        Assert.NotNull(server.WithDelivery(DeliveryMethod.Unreliable, (byte)(channels - 1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => server.WithDelivery(DeliveryMethod.Unreliable, channels));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => server.WithDelivery(DeliveryMethod.ReliableSequenced, 100));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => server.WithDelivery((DeliveryMethod)9, 0));
    }

    [Fact]
    public async Task ClientReceive_WhileItsSendPathIsBusy_KeepsDelivering()
    {
        (ushort tcpPort, ushort udpPort) = (NextPort(), NextPort());
        await using NetworkApplication app = await this.StartServerAsync(tcpPort, udpPort);
        IConnectionHub hub = InstanceManager.Instance.GetExistingInstance<IConnectionHub>()!;

        using TcpSession stream = await ConnectStreamAsync(tcpPort);
        using DatagramSession datagram = new(stream);
        await datagram.ConnectAsync().WaitAsync(s_timeout);

        DatagramConnection server = hub.ListConnections().OfType<DatagramConnection>().Single();
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        datagram.OnMessageReceived += (_, _) => received.TrySetResult();

        // Hold the client's send lock, as a thread blocked in a slow socket send does. Sends happen outside the
        // engine lock, so the receive loop still processes and delivers what the server pushes.
        Lock sendLock = (Lock)typeof(DatagramSession).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(datagram)!;
        sendLock.Enter();
        try
        {
            await server.SendAsync(new EchoPacket { Sequence = 7, Text = "push" }.Serialize(), DeliveryMethod.Unreliable, 1);
            Assert.True(received.Task.Wait(s_timeout), "the client must receive while its send path is busy");
        }
        finally
        {
            sendLock.Exit();
        }

        // Once the send path is free again, what was queued meanwhile goes out and the session keeps working.
        EchoPacket response = await datagram.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 1, Text = "after" }, s_request).AsTask().WaitAsync(s_timeout);
        Assert.Equal("AFTER", response.Text);
    }

    private static async Task<TcpSession> ConnectStreamAsync(ushort port)
    {
        TcpSession stream = new(CreateOptions(port));
        await stream.ConnectAsync().WaitAsync(s_timeout);
        await stream.HandshakeAsync().AsTask().WaitAsync(s_timeout);
        return stream;
    }

    private async Task<NetworkApplication> StartServerAsync(ushort tcpPort, ushort udpPort, IConnectionGuard? guard = null)
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        _ = builder.UseSecureConnections(_certificatePath).UseTimeSync();
        if (guard is not null)
        {
            _ = builder.UseConnectionGuard(guard);
        }

        _ = builder.MapHandlers(typeof(EchoHandlers));
        _ = builder.MapTcp<DefaultProtocol>().OnPort(tcpPort).Bind();
        _ = builder.MapDatagram<DefaultProtocol>().OnPort(udpPort).Bind();

        NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(s_timeout);
        return app;
    }

    private static TransportOptions CreateOptions(ushort port) => new()
    {
        Address = Host,
        Port = port,
        CompressionEnabled = false,
        ConnectTimeoutMillis = 5_000,
    };

    private static ushort NextPort() => (ushort)TestPorts.Next();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }
}
