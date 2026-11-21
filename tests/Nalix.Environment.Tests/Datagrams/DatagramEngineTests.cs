// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Options;

namespace Nalix.Environment.Tests.Datagrams;

/// <summary>
/// End-to-end behaviour of two <see cref="DatagramEngine"/> instances over <see cref="NetworkEmulator"/>.
/// </summary>
public sealed class DatagramEngineTests
{
    [Fact]
    public void Session_WhenClientSendsFirstPing_EstablishesBothSides()
    {
        using NetworkEmulator net = new(seed: 1);

        Assert.True(net.RunUntil(() => net.Client.State == DatagramEngineState.Established && net.Server.State == DatagramEngineState.Established));
        Assert.NotNull(net.Server.ActivePath);
        Assert.True(net.Client.SmoothedRtt > 0);
    }

    [Theory]
    [InlineData(11, 0.0)]
    [InlineData(12, 0.1)]
    [InlineData(13, 0.3)]
    public void ReliableOrdered_UnderLossReorderAndDuplication_DeliversEveryMessageOnceInOrder(int seed, double loss)
    {
        using NetworkEmulator net = new(seed) { LossRate = loss, DuplicateRate = 0.05, Jitter = 15_000 };
        const int count = 1500;
        System.Random sizes = new(seed);

        int sent = 0;
        net.AfterStep = () =>
        {
            while (sent < count && net.Client.TrySend(Message(sent, sizes.Next(1, 600)), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                sent++;
            }
        };

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 600_000_000));
        Assert.Equal(Enumerable.Range(0, count), net.ServerHandler.Messages.Select(m => Index(m.Data)));
        Assert.All(net.ServerHandler.Messages, m => Assert.Equal(DeliveryMethod.ReliableOrdered, m.Method));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(103)]
    [InlineData(104)]
    [InlineData(105)]
    [InlineData(106)]
    [InlineData(107)]
    public void MixedMethods_AcrossSeeds_KeepEveryDeliveryGuarantee(int seed)
    {
        using NetworkEmulator net = new(seed) { LossRate = 0.25, DuplicateRate = 0.1, Jitter = 40_000 };
        System.Random random = new(seed);
        const int count = 600;
        int sent = 0;
        int[] sentPerChannel = new int[4];

        net.AfterStep = () =>
        {
            while (sent < count)
            {
                byte channel = (byte)random.Next(4);
                int size = random.Next(4) == 0 ? random.Next(2_000, 12_000) : random.Next(8, 400);
                byte[] message = Message(sentPerChannel[channel], size);
                if (!net.Client.TrySend(message, DeliveryMethod.ReliableOrdered, channel, (byte)random.Next(8), net.Now))
                {
                    break;
                }

                sentPerChannel[channel]++;
                sent++;
            }
        };

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 900_000_000));

        // Per channel: every message exactly once and in order.
        for (byte channel = 0; channel < 4; channel++)
        {
            List<int> received = net.ServerHandler.Messages.Where(m => m.Channel == channel).Select(m => Index(m.Data)).ToList();
            Assert.Equal(Enumerable.Range(0, sentPerChannel[channel]), received);
        }
    }

    [Fact]
    public void ReliableUnordered_UnderLoss_DeliversEveryMessageExactlyOnce()
    {
        using NetworkEmulator net = new(seed: 21) { LossRate = 0.2, DuplicateRate = 0.1, Jitter = 30_000 };
        const int count = 1000;

        int sent = 0;
        net.AfterStep = () =>
        {
            while (sent < count && net.Client.TrySend(Message(sent, 64), DeliveryMethod.ReliableUnordered, 1, 0, net.Now))
            {
                sent++;
            }
        };

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count >= count, 600_000_000));
        net.RunUntil(() => false, 2_000_000);

        List<int> received = net.ServerHandler.Messages.Select(m => Index(m.Data)).ToList();
        Assert.Equal(count, received.Count);
        Assert.Equal(Enumerable.Range(0, count), received.Order());
    }

    [Theory]
    [InlineData(DeliveryMethod.ReliableOrdered)]
    [InlineData(DeliveryMethod.ReliableUnordered)]
    public void Fragmentation_WhenMessagesExceedOneDatagram_ReassemblesThemUnderLoss(DeliveryMethod method)
    {
        using NetworkEmulator net = new(seed: 31) { LossRate = 0.1, Jitter = 20_000 };
        byte[][] messages = [.. Enumerable.Range(0, 6).Select(i => Payload(i, 20_000 + (i * 37_123)))];

        foreach (byte[] message in messages)
        {
            Assert.True(net.Client.TrySend(message, method, 2, 0, net.Now));
        }

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == messages.Length, 600_000_000));

        IEnumerable<byte[]> received = net.ServerHandler.Messages.Select(m => m.Data);
        if (method == DeliveryMethod.ReliableOrdered)
        {
            Assert.Equal(messages, received);
        }
        else
        {
            Assert.Equal(messages.OrderBy(m => m.Length), received.OrderBy(m => m.Length));
        }
    }

    [Theory]
    [InlineData(DeliveryMethod.UnreliableSequenced)]
    [InlineData(DeliveryMethod.ReliableSequenced)]
    public void Sequenced_NeverDeliversAnOlderMessageAfterANewerOne(DeliveryMethod method)
    {
        using NetworkEmulator net = new(seed: 41) { LossRate = 0.15, Jitter = 40_000, DuplicateRate = 0.1 };
        const int count = 400;
        int sent = 0;
        long lastSend = 0;

        net.AfterStep = () =>
        {
            if (sent < count && net.Now - lastSend >= 5_000 && net.Client.TrySend(Message(sent, 32), method, 3, 0, net.Now))
            {
                sent++;
                lastSend = net.Now;
            }
        };

        _ = net.RunUntil(() => sent == count && net.Client.BytesInFlight == 0, 120_000_000);
        net.RunUntil(() => false, 3_000_000);

        List<int> received = net.ServerHandler.Messages.Select(m => Index(m.Data)).ToList();
        Assert.NotEmpty(received);
        Assert.True(received.Zip(received.Skip(1)).All(p => p.First < p.Second), "Sequenced delivery went backwards.");

        if (method == DeliveryMethod.ReliableSequenced)
        {
            Assert.Equal(count - 1, received[^1]); // The newest message is guaranteed.
        }
    }

    [Fact]
    public void Unreliable_IsDeliveredAtMostOnce()
    {
        using NetworkEmulator net = new(seed: 51) { LossRate = 0.2, DuplicateRate = 0.3 };
        _ = net.RunUntil(() => net.Client.State == DatagramEngineState.Established);

        // One message per datagram, so that losses are visible and never repaired.
        for (int i = 0; i < 50; i++)
        {
            Assert.True(net.Client.TrySend(Message(i, 16), DeliveryMethod.Unreliable, 0, 0, net.Now));
            net.Step();
        }

        net.RunUntil(() => false, 2_000_000);

        List<int> received = net.ServerHandler.Messages.Select(m => Index(m.Data)).ToList();
        Assert.Equal(received.Count, received.Distinct().Count());
        Assert.InRange(received.Count, 1, 49);
    }

    [Fact]
    public void Unreliable_WhenLargerThanOneDatagram_IsRejected()
    {
        using NetworkEmulator net = new(seed: 52);

        Assert.False(net.Client.TrySend(new byte[DatagramEngine.MaxUnfragmentedMessageSize + 1], DeliveryMethod.Unreliable, 0, 0, net.Now));
        Assert.False(net.Client.TrySend(new byte[DatagramEngine.MaxUnfragmentedMessageSize + 1], DeliveryMethod.ReliableSequenced, 0, 0, net.Now));
        Assert.True(net.Client.TrySend(new byte[DatagramEngine.MaxUnfragmentedMessageSize], DeliveryMethod.Unreliable, 0, 0, net.Now));
    }

    [Fact]
    public void Channels_WhenOneChannelHasAGap_OtherChannelsKeepDelivering()
    {
        using NetworkEmulator net = new(seed: 61);
        _ = net.RunUntil(() => net.Server.State == DatagramEngineState.Established && net.Client.State == DatagramEngineState.Established);

        // Lose the first datagram that carries channel 0 data.
        bool dropped = false;
        net.Drop = (fromClient, data) => fromClient && !dropped && (dropped = data.Length > 100);

        Assert.True(net.Client.TrySend(Payload(0, 200), DeliveryMethod.ReliableOrdered, 0, 0, net.Now));
        net.Step();
        Assert.True(dropped);

        Assert.True(net.Client.TrySend(Payload(1, 10), DeliveryMethod.ReliableOrdered, 1, 0, net.Now));
        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count >= 1, 5_000_000));
        Assert.Equal(1, net.ServerHandler.Messages[0].Channel);

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == 2, 10_000_000));
        Assert.Equal(0, net.ServerHandler.Messages[1].Channel);
    }

    [Fact]
    public void FlowControl_KeepsHeldBytesWithinTheAdvertisedBudget()
    {
        DatagramEngineOptions small = new() { ReceiveBufferBytes = 1_200_000, ChannelReceiveBufferBytes = 1_200_000, MaxMessageSize = 64 * 1024 };
        using NetworkEmulator net = new(seed: 71, serverOptions: small) { LossRate = 0.2, Jitter = 50_000 };
        long maxHeld = 0;
        int sent = 0;
        const int count = 3000;

        net.AfterStep = () =>
        {
            maxHeld = Math.Max(maxHeld, net.Server.ReceiverHeldBytes);
            while (sent < count && net.Client.TrySend(Message(sent, 900), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                sent++;
            }
        };

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 900_000_000));
        Assert.InRange(maxHeld, 1, small.ChannelReceiveBufferBytes);
    }

    [Fact]
    public async Task AckReceipt_CompletesWhenEveryFragmentIsAcknowledged()
    {
        using NetworkEmulator net = new(seed: 81) { LossRate = 0.1 };

        Task<bool>? receipt = net.Client.TrySendWithReceipt(Payload(5, 30_000), DeliveryMethod.ReliableOrdered, 0, 0, net.Now);
        Assert.NotNull(receipt);

        Assert.True(net.RunUntil(() => receipt.IsCompleted, 60_000_000));
        Assert.True(await receipt);
    }

    [Fact]
    public async Task AckReceipt_WhenSessionCloses_CompletesWithFalse()
    {
        using NetworkEmulator net = new(seed: 82) { Connected = false };

        Task<bool>? receipt = net.Client.TrySendWithReceipt(Payload(5, 100), DeliveryMethod.ReliableUnordered, 0, 0, net.Now);
        net.Client.Close(DatagramCloseCode.NoError, null, net.Now);

        Assert.False(await receipt!);
    }

    [Fact]
    public void Close_IsSignalledToThePeerWithCodeAndReason()
    {
        using NetworkEmulator net = new(seed: 91);
        _ = net.RunUntil(() => net.Server.State == DatagramEngineState.Established);

        net.Client.Close(DatagramCloseCode.ApplicationError, "bye", net.Now, applicationCode: 42);

        Assert.True(net.RunUntil(() => net.ServerHandler.Closed is not null));
        Assert.Equal((DatagramCloseCode.ApplicationError, 42UL, "bye", true), net.ServerHandler.Closed);
        Assert.Equal((DatagramCloseCode.ApplicationError, 42UL, "bye", false), net.ClientHandler.Closed);

        Assert.True(net.RunUntil(() => net.Client.State == DatagramEngineState.Closed && net.Server.State == DatagramEngineState.Closed));
        Assert.Equal(1, net.ServerHandler.CloseCount);
        Assert.Equal(1, net.ClientHandler.CloseCount);
    }

    [Fact]
    public void IdleTimeout_WhenThePeerGoesSilent_ClosesLocally()
    {
        using NetworkEmulator net = new(seed: 92, new DatagramEngineOptions { IdleTimeoutMs = 2000 }, new DatagramEngineOptions { IdleTimeoutMs = 2000 });
        _ = net.RunUntil(() => net.Server.State == DatagramEngineState.Established && net.Client.State == DatagramEngineState.Established);

        // Keepalives hold the session open while the network works.
        net.RunUntil(() => false, 10_000_000);
        Assert.Equal(DatagramEngineState.Established, net.Client.State);

        net.Connected = false;
        Assert.True(net.RunUntil(() => net.Client.State == DatagramEngineState.Closed && net.Server.State == DatagramEngineState.Closed, 30_000_000));
        Assert.Equal(DatagramCloseCode.IdleTimeout, net.ClientHandler.Closed!.Value.Code);
        Assert.False(net.ClientHandler.Closed!.Value.Remote);
    }

    [Fact]
    public void KeyUpdate_DuringTransfer_KeepsDelivering()
    {
        DatagramEngineOptions options = new() { KeyUpdatePacketInterval = 16 };
        using NetworkEmulator net = new(seed: 93, options, options) { LossRate = 0.05, Jitter = 10_000 };
        const int count = 600;
        int sent = 0;

        net.AfterStep = () =>
        {
            while (sent < count && net.Client.TrySend(Message(sent, 400), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                sent++;
            }
        };

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 300_000_000));
        Assert.Equal(Enumerable.Range(0, count), net.ServerHandler.Messages.Select(m => Index(m.Data)));
    }

    [Fact]
    public void Replay_OfAnAuthenticDatagram_IsDropped()
    {
        using NetworkEmulator net = new(seed: 94);
        _ = net.RunUntil(() => net.Server.State == DatagramEngineState.Established);

        byte[] first = net.ClientSent[0];
        long dropped = net.Server.DatagramsDropped;

        Assert.False(net.Server.Receive(first, net.ClientAddress, net.Now));
        Assert.Equal(dropped + 1, net.Server.DatagramsDropped);
    }

    [Fact]
    public void Receive_WhenDatagramIsTamperedOrMalformed_DropsWithoutThrowing()
    {
        using NetworkEmulator net = new(seed: 95);
        _ = net.RunUntil(() => net.Server.State == DatagramEngineState.Established);
        net.Client.TrySend(Payload(1, 100), DeliveryMethod.ReliableOrdered, 0, 0, net.Now);
        net.Connected = false;
        net.Step();
        byte[] valid = net.ClientSent[^1];

        System.Random random = new(7);
        for (int i = 0; i < 2000; i++)
        {
            byte[] fuzz = (byte[])valid.Clone();
            fuzz[random.Next(fuzz.Length)] ^= (byte)(1 << random.Next(8));
            Assert.False(net.Server.Receive(fuzz, net.ClientAddress, net.Now));

            byte[] noise = new byte[random.Next(0, 1500)];
            random.NextBytes(noise);
            Assert.False(net.Server.Receive(noise, net.ClientAddress, net.Now));
        }

        Assert.Equal(DatagramEngineState.Established, net.Server.State);
    }

    [Fact]
    public void NatRebinding_ServerValidatesTheNewAddressAndKeepsTheSession()
    {
        using NetworkEmulator net = new(seed: 96);
        _ = net.RunUntil(() => net.Server.State == DatagramEngineState.Established && net.Client.State == DatagramEngineState.Established);
        SocketAddress original = net.Server.ActivePath!;

        net.ClientAddress = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 41234).Serialize();
        int sent = 0;
        net.AfterStep = () =>
        {
            if (sent < 50 && net.Client.TrySend(Message(sent, 50), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                sent++;
            }
        };

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == 50, 30_000_000));
        Assert.True(net.RunUntil(() => net.Server.ActivePath!.Equals(net.ClientAddress), 10_000_000));
        Assert.False(original.Equals(net.Server.ActivePath));

        // Server-to-client traffic reaches the new address.
        Assert.True(net.Server.TrySend(Payload(9, 20), DeliveryMethod.ReliableOrdered, 0, 0, net.Now));
        Assert.True(net.RunUntil(() => net.ClientHandler.Messages.Count == 1, 10_000_000));
    }

    [Fact]
    public void MtuProbing_FindsTheLargestCandidateThePathCarries()
    {
        using NetworkEmulator net = new(seed: 97) { PathMtu = 1420 };
        _ = net.RunUntil(() => net.Server.State == DatagramEngineState.Established && net.Client.State == DatagramEngineState.Established);

        Assert.True(net.RunUntil(() => net.Client.Mtu == 1400, 60_000_000));
        net.RunUntil(() => false, 30_000_000);
        Assert.Equal(1400, net.Client.Mtu);
    }

    [Fact]
    public void Bandwidth_IsSharedWithoutCollapse()
    {
        using NetworkEmulator net = new(seed: 98) { Bandwidth = 500_000, Delay = 30_000 };
        const int count = 2000;
        int sent = 0;

        net.AfterStep = () =>
        {
            while (sent < count && net.Client.TrySend(Message(sent, 1000), DeliveryMethod.ReliableOrdered, 0, 0, net.Now))
            {
                sent++;
            }
        };

        long start = net.Now;
        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == count, 120_000_000));

        // 2 MB over a 500 kB/s link takes at least 4 s; congestion control should keep it within 4x of that.
        long elapsed = net.Now - start;
        Assert.InRange(elapsed, 4_000_000, 16_000_000);
    }

    [Fact]
    public void RequestResponse_AServerFlushingBeforeItsHandlerReplies_SendsOneDatagramThatCarriesTheAck()
    {
        // A server flushes as soon as a request arrives and its handler replies a little later. The request
        // also acknowledges the previous reply, which frees flow-control budget: that used to make the flush send
        // an ACK-only datagram ahead of the reply, nearly one per request.
        using NetworkEmulator net = new(seed: 99);
        Assert.True(net.RunUntil(() => net.Client.State == DatagramEngineState.Established && net.Server.State == DatagramEngineState.Established));
        byte[] buffer = new byte[DatagramEngine.MaxDatagramSize];

        for (int i = 0; i < 20; i++)
        {
            Assert.True(net.Client.TrySend(Message(i, 64), DeliveryMethod.ReliableOrdered, 0, 0, net.Now));
            int requests = 0;
            int length;
            while ((length = net.Client.Poll(buffer, net.Now, out _)) > 0)
            {
                Assert.True(net.Server.Receive(buffer.AsSpan(0, length), net.ClientAddress, net.Now));
                requests++;
            }

            Assert.Equal(1, requests); // The request carries the client's ACK of the previous reply.

            // The flush on receipt: nothing is due, so nothing is sent.
            Assert.Equal(0, net.Server.Poll(buffer, net.Now, out _));

            // The handler replies: one datagram, with the reply and the ACK of the request.
            Assert.True(net.Server.TrySend(Message(i, 64), DeliveryMethod.ReliableOrdered, 0, 0, net.Now));
            int replies = 0;
            while ((length = net.Server.Poll(buffer, net.Now, out _)) > 0)
            {
                Assert.True(net.Client.Receive(buffer.AsSpan(0, length), from: null, net.Now));
                replies++;
            }

            Assert.Equal(1, replies);
        }

        Assert.Equal(20, net.ClientHandler.Messages.Count);
        Assert.Equal(20, net.ServerHandler.Messages.Count);
    }

    private static byte[] Message(int index, int size)
    {
        byte[] message = new byte[Math.Max(size, 4)];
        BinaryPrimitives.WriteInt32LittleEndian(message, index);
        return message;
    }

    private static int Index(byte[] message) => BinaryPrimitives.ReadInt32LittleEndian(message);

    private static byte[] Payload(int seed, int size)
    {
        byte[] payload = new byte[size];
        new System.Random(seed).NextBytes(payload);
        payload[(int)Nalix.Abstractions.Networking.Packets.PacketHeaderOffset.Flags] = 0; // Not a fragment header.
        return payload;
    }
}
