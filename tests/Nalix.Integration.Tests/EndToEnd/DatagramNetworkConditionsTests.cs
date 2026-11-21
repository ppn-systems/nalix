// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Codec.DataFrames;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Network.Connections;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Tests.Shared;
using Xunit;

namespace Nalix.Integration.Tests.EndToEnd;

/// <summary>
/// The Nalix Datagram Protocol over real UDP sockets through <see cref="ImpairedUdpProxy"/>: the SDK client and
/// the server, with loss, jitter, reordering, duplication, burst loss, an MTU black hole, NAT rebinding and a
/// mobile handover between them.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class DatagramNetworkConditionsTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(20);
    private static readonly RequestOptions s_request = RequestOptions.Default.WithTimeout(10_000);

    private readonly NetworkConfigSnapshot _configSnapshot = new();
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-ndp-net-{Guid.NewGuid():N}.private");

    public DatagramNetworkConditionsTests()
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
    public async Task Requests_OverALossyJitteryLinkThatReordersAndDuplicates_AllSucceed()
    {
        await using Harness h = await this.StartAsync(seed: 1);
        h.Proxy.LossRate = 0.10;
        h.Proxy.DuplicateRate = 0.03;
        h.Proxy.Delay = TimeSpan.FromMilliseconds(10);
        h.Proxy.Jitter = TimeSpan.FromMilliseconds(15);

        for (int i = 0; i < 40; i++)
        {
            await h.EchoAsync(i, $"lossy {i}");
        }

        Assert.True(h.Proxy.Dropped > 0, "The link must actually drop datagrams.");
    }

    [Fact]
    public async Task Requests_OverWiFiLikeBurstLoss_IncludingFragmentedOnes_AllSucceed()
    {
        await using Harness h = await this.StartAsync(seed: 2);
        h.Proxy.BurstStart = 0.04;
        h.Proxy.BurstEnd = 0.3;
        h.Proxy.Delay = TimeSpan.FromMilliseconds(5);
        h.Proxy.Jitter = TimeSpan.FromMilliseconds(5);

        System.Random random = new(5);
        for (int i = 0; i < 6; i++)
        {
            string text = new([.. Enumerable.Range(0, 20_000 + (i * 3_000)).Select(_ => (char)random.Next('a', 'z' + 1))]);
            await h.EchoAsync(i, text);
        }

        Assert.True(h.Proxy.Dropped > 0);
    }

    [Fact]
    public async Task NatRebinding_MidSession_TheServerFollowsTheNewPortAndRequestsKeepWorking()
    {
        await using Harness h = await this.StartAsync(seed: 3);
        await h.EchoAsync(0, "before");
        DatagramConnection server = h.ServerConnection();
        Assert.Equal(h.Proxy.NatPort, server.NetworkEndpoint.Port);

        // The NAT mapping changes: the server now sees the client on another port.
        h.Proxy.Rebind();
        for (int i = 1; i <= 10; i++)
        {
            await h.EchoAsync(i, $"after {i}");
        }

        Assert.True(h.Client.IsConnected);
        await WaitUntilAsync(() => server.NetworkEndpoint.Port == h.Proxy.NatPort);
    }

    [Fact]
    public async Task MobileHandover_BlackoutNewPortAndSlowerPath_ARequestSentDuringTheBlackoutCompletesAfterIt()
    {
        await using Harness h = await this.StartAsync(seed: 4);
        h.Proxy.Delay = TimeSpan.FromMilliseconds(5);
        await h.EchoAsync(0, "on wi-fi");

        bool disconnected = false;
        h.Client.OnDisconnected += (_, _) => disconnected = true;

        // Wi-Fi drops; a request leaves during the 2 s without network; the phone comes back on LTE.
        h.Proxy.Connected = false;
        Task inFlight = h.EchoAsync(1, "during the blackout");
        await Task.Delay(TimeSpan.FromSeconds(2));
        h.Proxy.Rebind();
        h.Proxy.Delay = TimeSpan.FromMilliseconds(40);
        h.Proxy.Jitter = TimeSpan.FromMilliseconds(20);
        h.Proxy.LossRate = 0.02;
        h.Proxy.Connected = true;

        await inFlight.WaitAsync(s_timeout);
        for (int i = 2; i < 12; i++)
        {
            await h.EchoAsync(i, $"on lte {i}");
        }

        Assert.False(disconnected);
        Assert.True(h.Client.IsConnected);
        await WaitUntilAsync(() => h.ServerConnection().NetworkEndpoint.Port == h.Proxy.NatPort);
    }

    [Fact]
    public async Task MtuBlackHole_LargeRequestsStillGetThrough()
    {
        await using Harness h = await this.StartAsync(seed: 5);

        // Let MTU discovery climb on the clean path first, then put the session behind a 1300-byte tunnel.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await h.EchoAsync(0, "probe");
        h.Proxy.MaxDatagramSize = 1300;

        System.Random random = new(6);
        for (int i = 1; i <= 3; i++)
        {
            string text = new([.. Enumerable.Range(0, 30_000).Select(_ => (char)random.Next('a', 'z' + 1))]);
            await h.EchoAsync(i, text);
        }
    }

    [Fact]
    public async Task ServerPush_UnreliableUnderLossAndReliableOrderedOnAnotherChannel_KeepTheirGuarantees()
    {
        await using Harness h = await this.StartAsync(seed: 7);
        h.Proxy.LossRate = 0.15;
        h.Proxy.Jitter = TimeSpan.FromMilliseconds(10);

        System.Collections.Concurrent.ConcurrentQueue<int> unreliable = new();
        System.Collections.Concurrent.ConcurrentQueue<int> reliable = new();
        h.Client.OnMessageReceived += (_, lease) =>
        {
            EchoPacket packet = EchoPacket.Deserialize(lease.Span);
            (packet.Text == "u" ? unreliable : reliable).Enqueue(packet.Sequence);
        };

        DatagramConnection server = h.ServerConnection();
        IConnection.ITransport unreliableView = server.WithDelivery(DeliveryMethod.Unreliable, 1);
        IConnection.ITransport reliableView = server.WithDelivery(DeliveryMethod.ReliableOrdered, 2);
        for (int i = 0; i < 100; i++)
        {
            await unreliableView.SendAsync(new EchoPacket { Sequence = i, Text = "u" }.Serialize());
            await reliableView.SendAsync(new EchoPacket { Sequence = i, Text = "r" }.Serialize());
            await Task.Delay(2);
        }

        await WaitUntilAsync(() => reliable.Count == 100);
        await Task.Delay(500);

        Assert.Equal(Enumerable.Range(0, 100), reliable);
        Assert.Equal(unreliable.Count, unreliable.Distinct().Count()); // at most once
        Assert.InRange(unreliable.Count, 50, 99); // most arrive; the losses are not repaired
    }

    private async Task<Harness> StartAsync(int seed)
    {
        (ushort tcpPort, ushort udpPort) = ((ushort)TestPorts.Next(), (ushort)TestPorts.Next());

        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        _ = builder.UseSecureConnections(_certificatePath).UseTimeSync();
        _ = builder.MapHandlers(typeof(EchoHandlers));
        _ = builder.MapTcp<DefaultProtocol>().OnPort(tcpPort).Bind();
        _ = builder.MapDatagram<DefaultProtocol>().OnPort(udpPort).Bind();
        NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(s_timeout);

        ImpairedUdpProxy proxy = new(new IPEndPoint(IPAddress.Loopback, udpPort), seed);

        TcpSession stream = new(new TransportOptions { Address = Host, Port = tcpPort, CompressionEnabled = false, ConnectTimeoutMillis = 5_000 });
        await stream.ConnectAsync().WaitAsync(s_timeout);
        await stream.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        // The ticket comes over TCP; the datagrams go through the proxy.
        DatagramSession client = new(stream);
        await client.ConnectAsync(Host, proxy.Port).WaitAsync(s_timeout);

        return new Harness(app, proxy, stream, client);
    }

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

    private sealed class Harness(NetworkApplication app, ImpairedUdpProxy proxy, TcpSession stream, DatagramSession client) : IAsyncDisposable
    {
        public ImpairedUdpProxy Proxy { get; } = proxy;

        public DatagramSession Client { get; } = client;

        public DatagramConnection ServerConnection()
            => InstanceManager.Instance.GetExistingInstance<IConnectionHub>()!.ListConnections().OfType<DatagramConnection>().Single();

        public async Task EchoAsync(int sequence, string text)
        {
            EchoPacket response = await this.Client.RequestAsync<EchoPacket>(
                new EchoPacket { Sequence = sequence, Text = text }, s_request).AsTask().WaitAsync(s_timeout);

            Assert.Equal(sequence + 1, response.Sequence);
            Assert.Equal(text.ToUpperInvariant(), response.Text);
        }

        public async ValueTask DisposeAsync()
        {
            this.Client.Dispose();
            stream.Dispose();
            this.Proxy.Dispose();
            await app.DisposeAsync();
        }
    }
}
