// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Options;

namespace Nalix.Environment.Tests.Datagrams;

/// <summary>
/// Network conditions met in the field (Wi-Fi burst loss, a shallow bottleneck queue, mobile handover, NAT
/// rebinding, an MTU that shrinks, spoofed and replayed datagrams), one protocol component checked per test:
/// RTT estimation, loss detection, NewReno, pacing, MTU discovery, path validation, replay protection, key update
/// and the delivery channels. Every run is deterministic for its seed.
/// </summary>
public sealed class DatagramNetworkScenarioTests
{
    private const long Ms = 1_000;
    private const long Second = 1_000_000;

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(203)]
    public void BurstLoss_LikeWiFi_DeliversEveryReliableMessageOnceInOrder(int seed)
    {
        // About 9 % loss in bursts of ~3 datagrams, with reordering.
        using NetworkEmulator net = new(seed) { BurstStart = 0.03, BurstEnd = 0.35, Delay = 15 * Ms, Jitter = 10 * Ms };
        const int count = 800;
        Feed(net, count, size: 300, DeliveryMethod.ReliableOrdered, interval: 2 * Ms);

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 300 * Second));
        Assert.Equal(Enumerable.Range(0, count), net.ServerHandler.Messages.Select(m => Index(m.Data)));
        Assert.True(net.ClientDropped + net.ServerDropped > 10, $"The scenario must actually lose datagrams ({net.ClientDropped} of {net.ClientSent.Count}, server {net.ServerDropped}).");
    }

    [Fact]
    public void BurstLoss_WithFragmentedMessages_ReassemblesEveryOne()
    {
        using NetworkEmulator net = new(seed: 204) { BurstStart = 0.03, BurstEnd = 0.3, Jitter = 8 * Ms };
        byte[][] messages = [.. Enumerable.Range(0, 8).Select(i => Message(i, 30_000 + (i * 7_919)))];

        foreach (byte[] message in messages)
        {
            Assert.True(net.Client.TrySend(message, DeliveryMethod.ReliableOrdered, 1, 0, net.Now));
        }

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == messages.Length, 600 * Second));
        Assert.Equal(messages, net.ServerHandler.Messages.Select(m => m.Data));
    }

    [Fact]
    public void RttEstimate_TracksThePathDelayAndFollowsItWhenItChanges()
    {
        using NetworkEmulator net = new(seed: 210) { Delay = 40 * Ms, Jitter = 4 * Ms };
        Feed(net, int.MaxValue, size: 100, DeliveryMethod.ReliableOrdered, interval: 20 * Ms);

        // RTT 80 ms plus up to 4 ms of jitter; the ACK delay is reported and taken out.
        net.RunUntil(() => false, 5 * Second);
        Assert.InRange(net.Client.SmoothedRtt, 78 * Ms, 100 * Ms);

        // The path gets slower (a mobile cell under load): RTT 300 ms.
        net.Delay = 150 * Ms;
        net.RunUntil(() => false, 10 * Second);
        Assert.InRange(net.Client.SmoothedRtt, 290 * Ms, 330 * Ms);

        // And fast again.
        net.Delay = 40 * Ms;
        net.RunUntil(() => false, 10 * Second);
        Assert.InRange(net.Client.SmoothedRtt, 78 * Ms, 110 * Ms);
    }

    [Fact]
    public void LossDetection_WithoutLossAndWithLightReordering_DeclaresNothingLost()
    {
        // Datagrams 5 ms apart, reordered by up to 3 ms: never three packets overtaking one.
        using NetworkEmulator net = new(seed: 211) { Delay = 30 * Ms, Jitter = 3 * Ms };
        const int count = 500;
        Feed(net, count, size: 200, DeliveryMethod.ReliableOrdered, interval: 5 * Ms);

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count && net.Client.BytesInFlight == 0, 120 * Second));
        Assert.Equal(0, net.Client.DatagramsLost);
    }

    [Fact]
    public void LossDetection_UnderRandomLoss_CountsAboutTheDatagramsTheNetworkDropped()
    {
        using NetworkEmulator net = new(seed: 212) { Delay = 30 * Ms, Jitter = 2 * Ms, LossRate = 0.05 };
        const int count = 1500;
        Feed(net, count, size: 200, DeliveryMethod.ReliableOrdered, interval: 4 * Ms);

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count && net.Client.BytesInFlight == 0, 300 * Second));

        // Every drop is detected (by packet or time threshold, or by a probe), and few are declared spuriously.
        double ratio = (double)net.Client.DatagramsLost / net.ClientDropped;
        Assert.InRange(ratio, 0.8, 1.3);
    }

    [Fact]
    public void NewReno_OnABottleneckWithAShallowQueue_UsesTheLinkWithoutCollapse()
    {
        // 1 MB/s, RTT 50 ms (a 50 kB path), a 30 kB drop-tail queue: the sender must back off on loss and recover.
        using NetworkEmulator net = new(seed: 213) { Bandwidth = 1_000_000, Delay = 25 * Ms, QueueBytes = 30_000 };
        const int count = 3000;
        int sent = 0;
        long minWindowAfterStart = long.MaxValue;
        long maxWindow = 0;

        net.AfterStep = () =>
        {
            while (sent < count && net.Client.TrySend(Message(sent, 1000), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                sent++;
            }

            maxWindow = Math.Max(maxWindow, net.Client.CongestionWindow);
            if (maxWindow > 100_000)
            {
                minWindowAfterStart = Math.Min(minWindowAfterStart, net.Client.CongestionWindow);
            }
        };

        long start = net.Now;
        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 120 * Second));
        double seconds = (net.Now - start) / (double)Second;

        // 3 MB at 1 MB/s takes 3 s; NewReno on a shallow queue should keep it under 4.5 s (two-thirds of the link).
        Assert.InRange(seconds, 3.0, 4.5);
        Assert.True(net.ClientDropped > 0, "The queue must overflow, or the test checks nothing.");
        Assert.True(net.ClientDropped < sent / 10, $"Losses stay a small share: {net.ClientDropped} of {sent}.");

        // The window was cut on loss, but never to the persistent-congestion minimum.
        Assert.True(minWindowAfterStart < maxWindow);
        Assert.True(minWindowAfterStart > 2L * net.Client.Mtu);
    }

    [Fact]
    public void Pacing_SpreadsALargeWindowOverTheRoundTrip()
    {
        using NetworkEmulator net = new(seed: 214) { Delay = 50 * Ms };
        int sent = 0;
        net.AfterStep = () =>
        {
            while (sent < 4000 && net.Client.TrySend(Message(sent, 1000), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                sent++;
            }
        };

        // Let slow start open the window far beyond the burst allowance.
        Assert.True(net.RunUntil(() => net.Client.CongestionWindow > 200_000, 60 * Second));
        int from = net.ClientSent.Count;
        net.RunUntil(() => false, 1 * Second);

        // Datagrams leaving at the same instant: at most the pacer's ten-datagram burst, never the whole window.
        int largestBurst = net.ClientSendTimes.Skip(from).GroupBy(t => t).Max(g => g.Count());
        Assert.InRange(largestBurst, 1, 11);

        // And the sender still keeps the window busy: well over a window per round trip is sent in that second.
        Assert.True(net.ClientSent.Count - from > 10 * 100, $"Only {net.ClientSent.Count - from} datagrams in 1 s.");
    }

    [Fact]
    public void MtuBlackHole_AppearingMidSession_FallsBackKeepsDeliveringAndLaterFindsTheNewLimit()
    {
        using NetworkEmulator net = new(seed: 215);
        Assert.True(net.RunUntil(() => net.Client.Mtu == 1472, 60 * Second));

        // The route changes to one that carries 1300 bytes at most (a tunnel), and says nothing about it.
        net.PathMtu = 1300;
        const int count = 300;
        Feed(net, count, size: 1100, DeliveryMethod.ReliableOrdered, interval: 1 * Ms);
        Assert.True(net.Client.TrySend(Message(-1, 40_000), DeliveryMethod.ReliableOrdered, 1, 0, net.Now));

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count + 1, 50 * Second));
        Assert.Equal(DatagramEngineOptions.MinimumMtu, net.Client.Mtu);
        Assert.Equal(Enumerable.Range(0, count), net.ServerHandler.Messages.Where(m => m.Channel == 0).Select(m => Index(m.Data)));

        // After the raise delay the search runs again and stops at the largest candidate the route carries.
        Feed(net, int.MaxValue, size: 100, DeliveryMethod.ReliableOrdered, interval: 50 * Ms);
        Assert.True(net.RunUntil(() => net.Client.Mtu == 1232, 90 * Second));
        net.RunUntil(() => false, 30 * Second);
        Assert.Equal(1232, net.Client.Mtu);
    }

    [Fact]
    public void MobileHandover_BlackoutNewAddressAndSlowerPath_TheSessionSurvivesAndCatchesUp()
    {
        using NetworkEmulator net = new(seed: 216) { Delay = 20 * Ms, Jitter = 5 * Ms };
        const int count = 600;
        Feed(net, count, size: 200, DeliveryMethod.ReliableOrdered, interval: 10 * Ms);
        net.RunUntil(() => false, 2 * Second);

        // Wi-Fi to LTE: 3 s without any network, then a new address and a slower, lossier path.
        net.Connected = false;
        net.RunUntil(() => false, 3 * Second);
        net.ClientAddress = new IPEndPoint(IPAddress.Parse("100.64.7.9"), 52011).Serialize();
        net.Delay = 70 * Ms;
        net.LossRate = 0.02;
        net.Connected = true;

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 120 * Second));
        Assert.Equal(Enumerable.Range(0, count), net.ServerHandler.Messages.Select(m => Index(m.Data)));
        Assert.Equal(DatagramEngineState.Established, net.Client.State);
        Assert.Equal(DatagramEngineState.Established, net.Server.State);
        Assert.True(net.RunUntil(() => net.ClientAddress.Equals(net.Server.ActivePath), 10 * Second));
        Assert.True(net.Client.SmoothedRtt > 120 * Ms, $"SRTT {net.Client.SmoothedRtt} did not follow the slower path.");

        // The server reaches the client at its new address.
        Assert.True(net.Server.TrySend(Message(1, 50), DeliveryMethod.ReliableOrdered, 0, 0, net.Now));
        Assert.True(net.RunUntil(() => net.ClientHandler.Messages.Count == 1, 10 * Second));

        // The blackout looked like an MTU black hole (every large datagram lost); it must not cap the MTU for
        // the rest of the session.
        Feed(net, int.MaxValue, size: 100, DeliveryMethod.ReliableOrdered, interval: 50 * Ms);
        Assert.True(net.RunUntil(() => net.Client.Mtu == 1472, 120 * Second), $"MTU stayed at {net.Client.Mtu}.");
    }

    [Fact]
    public void Blackout_LongerThanTheIdleTimeout_ClosesBothSides()
    {
        DatagramEngineOptions options = new() { IdleTimeoutMs = 4_000 };
        using NetworkEmulator net = new(seed: 217, options, options);
        Feed(net, int.MaxValue, size: 100, DeliveryMethod.ReliableOrdered, interval: 50 * Ms);
        net.RunUntil(() => false, 2 * Second);

        net.Connected = false;
        Assert.True(net.RunUntil(() => net.Client.State == DatagramEngineState.Closed && net.Server.State == DatagramEngineState.Closed, 10 * Second));
        Assert.Equal(DatagramCloseCode.IdleTimeout, net.ClientHandler.Closed!.Value.Code);
        Assert.Equal(DatagramCloseCode.IdleTimeout, net.ServerHandler.Closed!.Value.Code);
    }

    [Fact]
    public void PathValidation_DatagramRacedFromASpoofedAddress_DoesNotMoveTheSession()
    {
        using NetworkEmulator net = new(seed: 218) { Delay = 30 * Ms };
        Assert.True(net.RunUntil(() => net.Server.State == DatagramEngineState.Established && net.Client.State == DatagramEngineState.Established));
        SocketAddress client = net.Server.ActivePath!;
        SocketAddress attacker = new IPEndPoint(IPAddress.Parse("198.51.100.66"), 6666).Serialize();

        // An on-path observer copies every client datagram and races it to the server from its own address.
        net.Drop = (fromClient, data) =>
        {
            if (fromClient)
            {
                net.Inject(data, attacker, delay: 1 * Ms);
            }

            return false;
        };

        const int count = 200;
        Feed(net, count, size: 100, DeliveryMethod.ReliableOrdered, interval: 10 * Ms);
        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 60 * Second));
        net.RunUntil(() => false, 5 * Second);

        // The attacker cannot answer the challenge, so the session stays on the client's address...
        Assert.True(client.Equals(net.Server.ActivePath));
        Assert.Equal(Enumerable.Range(0, count), net.ServerHandler.Messages.Select(m => Index(m.Data)));

        // ...and the server sent the spoofed address no more than three times what it received from it.
        long toAttacker = net.ServerBytesTo.GetValueOrDefault(attacker);
        long fromAttacker = net.ClientSent.Sum(d => (long)d.Length);
        Assert.InRange(toAttacker, 1, 3 * fromAttacker);
    }

    [Fact]
    public void ReplayProtection_EveryCapturedDatagramReplayedLater_IsDroppedWithoutEffect()
    {
        using NetworkEmulator net = new(seed: 219) { Jitter = 5 * Ms };
        const int count = 300;
        Feed(net, count, size: 120, DeliveryMethod.ReliableUnordered, interval: 2 * Ms);
        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count && net.Client.BytesInFlight == 0, 60 * Second));

        // Replayed from the client's own address and from another one, long after (beyond the replay window too).
        Feed(net, 5000, size: 20, DeliveryMethod.Unreliable, interval: 100);
        net.RunUntil(() => false, 2 * Second);
        int delivered = net.ServerHandler.Messages.Count;
        long dropped = net.Server.DatagramsDropped;
        SocketAddress path = net.Server.ActivePath!;
        SocketAddress other = new IPEndPoint(IPAddress.Parse("203.0.113.5"), 1234).Serialize();

        byte[][] captured = [.. net.ClientSent.Take(400)];
        foreach (byte[] datagram in captured)
        {
            Assert.False(net.Server.Receive(datagram, path, net.Now));
            Assert.False(net.Server.Receive(datagram, other, net.Now));
        }

        Assert.Equal(delivered, net.ServerHandler.Messages.Count);
        Assert.Equal(dropped + (2 * captured.Length), net.Server.DatagramsDropped);
        Assert.True(path.Equals(net.Server.ActivePath));
        Assert.Equal(DatagramEngineState.Established, net.Server.State);
    }

    [Fact]
    public void KeyUpdate_ManyTimesUnderBurstLossAndReorderingInBothDirections_KeepsDelivering()
    {
        DatagramEngineOptions options = new() { KeyUpdatePacketInterval = 32 };
        using NetworkEmulator net = new(seed: 220, options, options) { BurstStart = 0.03, BurstEnd = 0.3, Jitter = 15 * Ms };
        const int count = 1000;
        int clientSent = 0, serverSent = 0;
        long last = 0;

        net.AfterStep = () =>
        {
            if (net.Now - last < 3 * Ms)
            {
                return;
            }

            last = net.Now;
            if (clientSent < count && net.Client.TrySend(Message(clientSent, 200), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                clientSent++;
            }

            if (serverSent < count && net.Server.TrySend(Message(serverSent, 200), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                serverSent++;
            }
        };

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count && net.ClientHandler.Messages.Count == count, 600 * Second));
        Assert.Equal(Enumerable.Range(0, count), net.ServerHandler.Messages.Select(m => Index(m.Data)));
        Assert.Equal(Enumerable.Range(0, count), net.ClientHandler.Messages.Select(m => Index(m.Data)));

        // With a key update every 32 packets, well over ten updates happened in each direction.
        Assert.True(net.ClientSent.Count > 32 * 10);
    }

    [Fact]
    public void Channels_GameTrafficOnAMobilePath_PositionsStayFreshWhileChatIsReliable()
    {
        // 60 Hz unreliable-sequenced positions on channel 1, reliable-ordered chat on channel 0, over a lossy,
        // jittery mobile path.
        using NetworkEmulator net = new(seed: 221) { Delay = 45 * Ms, Jitter = 25 * Ms, BurstStart = 0.02, BurstEnd = 0.4, LossRate = 0.01 };
        const int positions = 600;
        const int chats = 60;
        int p = 0, c = 0;
        long lastPosition = 0, lastChat = 0;

        net.AfterStep = () =>
        {
            if (p < positions && net.Now - lastPosition >= 16_667 && net.Client.TrySend(Message(p, 48), DeliveryMethod.UnreliableSequenced, 1, 1, net.Now))
            {
                p++;
                lastPosition = net.Now;
            }

            if (c < chats && net.Now - lastChat >= 160 * Ms && net.Client.TrySend(Message(c, 120), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                c++;
                lastChat = net.Now;
            }
        };

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count(m => m.Channel == 0) == chats, 120 * Second));
        net.RunUntil(() => false, 2 * Second);

        List<int> chat = [.. net.ServerHandler.Messages.Where(m => m.Channel == 0).Select(m => Index(m.Data))];
        List<int> moves = [.. net.ServerHandler.Messages.Where(m => m.Channel == 1).Select(m => Index(m.Data))];

        Assert.Equal(Enumerable.Range(0, chats), chat);
        Assert.True(moves.Zip(moves.Skip(1)).All(pair => pair.First < pair.Second), "A stale position was delivered after a newer one.");

        // Losses and stale positions are dropped, not waited for: most still arrive.
        Assert.InRange(moves.Count, positions * 6 / 10, positions);
    }

    /// <summary>Sends <paramref name="count"/> indexed messages, one every <paramref name="interval"/> µs.</summary>
    private static void Feed(NetworkEmulator net, int count, int size, DeliveryMethod method, long interval)
    {
        int sent = 0;
        long next = net.Now;
        Action? previous = net.AfterStep;
        net.AfterStep = () =>
        {
            previous?.Invoke();
            while (sent < count && net.Now >= next && net.Client.TrySend(Message(sent, size), method, 0, 0, net.Now))
            {
                sent++;
                next += interval;
            }
        };
    }

    private static byte[] Message(int index, int size)
    {
        byte[] message = new byte[Math.Max(size, 4)];
        BinaryPrimitives.WriteInt32LittleEndian(message, index);
        return message;
    }

    private static int Index(byte[] message) => BinaryPrimitives.ReadInt32LittleEndian(message);
}
