// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System;
using Nalix.Environment.Datagrams.Congestion;
using Nalix.Environment.Datagrams.Recovery;
using Nalix.Environment.Datagrams.Wire;
using Nalix.Environment.Datagrams;

namespace Nalix.Environment.Tests.Datagrams;

/// <summary>
/// Wire primitives of the datagram engine: varints, packet number reconstruction, the replay window and
/// ACK ranges.
/// </summary>
public sealed class DatagramPrimitivesTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(63UL, 1)]
    [InlineData(64UL, 2)]
    [InlineData(16383UL, 2)]
    [InlineData(16384UL, 4)]
    [InlineData((1UL << 30) - 1, 4)]
    [InlineData(1UL << 30, 8)]
    [InlineData((1UL << 62) - 1, 8)]
    public void VarInt_RoundTripsWithTheRfc9000Sizes(ulong value, int size)
    {
        Span<byte> buffer = stackalloc byte[8];

        Assert.Equal(size, VarInt.Write(buffer, value));
        Assert.True(VarInt.TryRead(buffer, out ulong read, out int readSize));
        Assert.Equal(value, read);
        Assert.Equal(size, readSize);
        Assert.False(VarInt.TryRead(buffer[..(size - 1)], out _, out _));
    }

    [Fact]
    public void VarInt_DecodesTheRfc9000Example()
    {
        // RFC 9000 §A.1: 0x9d7f3e7d decodes to 494878333.
        Assert.True(VarInt.TryRead([0x9d, 0x7f, 0x3e, 0x7d], out ulong value, out _));
        Assert.Equal(494_878_333UL, value);
    }

    [Theory]
    [InlineData(-1L, 0u, 0UL)]
    [InlineData(10L, 11u, 11UL)]
    [InlineData(0xFFFF_FFF0L, 0x0000_0005u, 0x1_0000_0005UL)] // Wraps forward.
    [InlineData(0x1_0000_0002L, 0xFFFF_FFFEu, 0xFFFF_FFFEUL)] // Late packet from before the wrap.
    public void DecodePacketNumber_ReconstructsTheFullValue(long largest, uint truncated, ulong expected)
        => Assert.Equal(expected, DatagramHeader.DecodePacketNumber(truncated, largest));

    [Fact]
    public void Header_RejectsUnknownVersionAndReservedBits()
    {
        Span<byte> buffer = stackalloc byte[DatagramHeader.Size];
        new DatagramHeader(true, 42, 7).WriteTo(buffer);

        Assert.True(DatagramHeader.TryRead(buffer, out DatagramHeader header));
        Assert.True(header.KeyPhase);
        Assert.Equal(42UL, header.ConnectionId);
        Assert.Equal(7u, header.TruncatedPacketNumber);

        buffer[0] |= 0b0000_0001;
        Assert.False(DatagramHeader.TryRead(buffer, out _));

        buffer[0] = 0x20;
        Assert.False(DatagramHeader.TryRead(buffer, out _));
    }

    [Fact]
    public void ReplayWindow_RejectsDuplicatesAndPacketsOlderThanTheWindow()
    {
        ReplayWindow window = new();

        Assert.True(window.IsNew(5));
        window.Mark(5);
        Assert.False(window.IsNew(5));
        Assert.True(window.IsNew(3));
        window.Mark(3000);
        Assert.False(window.IsNew(3000 - ReplayWindow.Size));
        Assert.True(window.IsNew(3000 - ReplayWindow.Size + 1));
        Assert.True(window.IsNew(2999));

        // Checking does not change state.
        Assert.True(window.IsNew(2999));
        window.Mark(2999);
        Assert.False(window.IsNew(2999));
    }

    [Fact]
    public void AckTracker_EncodesRangesThatDecodeToTheReceivedPackets()
    {
        AckTracker tracker = new();
        HashSet<ulong> received = [0, 1, 2, 5, 6, 9, 20, 21, 22, 23];
        foreach (ulong pn in new ulong[] { 22, 0, 5, 1, 23, 9, 2, 6, 21, 20, 2 })
        {
            tracker.OnPacketReceived(pn, ackEliciting: true, now: 0, maxAckDelay: 25_000);
        }

        Span<byte> frame = stackalloc byte[256];
        int length = tracker.WriteAckFrame(frame, now: 0, out ulong largest);

        Assert.Equal(23UL, largest);
        Assert.Equal(received, Decode(frame[..length]));
        Assert.True(tracker.AckPending);

        tracker.OnAckSent();
        Assert.False(tracker.AckPending);

        // Once the peer has our ACK up to 9, only newer ranges are sent again.
        tracker.OnAckAcknowledged(9);
        length = tracker.WriteAckFrame(frame, now: 0, out _);
        Assert.Equal(new HashSet<ulong> { 20, 21, 22, 23 }, Decode(frame[..length]));
    }

    [Fact]
    public void AckTracker_AcknowledgesAtOnceOnAGapAndShortlyAfterTwoPackets()
    {
        AckTracker tracker = new();

        tracker.OnPacketReceived(0, true, now: 100, maxAckDelay: 25_000);
        Assert.Equal(25_100, tracker.AckDeadline);

        // The second packet brings the ACK forward to the coalescing delay, for a reply to carry it.
        tracker.OnPacketReceived(1, true, now: 200, maxAckDelay: 25_000);
        Assert.Equal(200 + AckTracker.AckCoalesceDelay, tracker.AckDeadline);

        // A gap is acknowledged at once.
        tracker.OnAckSent();
        tracker.OnPacketReceived(5, true, now: 300, maxAckDelay: 25_000);
        Assert.Equal(300, tracker.AckDeadline);

        // Never later than the peer's max ACK delay.
        tracker.OnAckSent();
        tracker.OnPacketReceived(6, true, now: 400, maxAckDelay: 500);
        tracker.OnPacketReceived(7, true, now: 450, maxAckDelay: 500);
        Assert.Equal(900, tracker.AckDeadline);
    }

    private static HashSet<ulong> Decode(ReadOnlySpan<byte> frame)
    {
        int offset = 1;
        ulong Read(ReadOnlySpan<byte> f)
        {
            Assert.True(VarInt.TryRead(f[offset..], out ulong v, out int size));
            offset += size;
            return v;
        }

        HashSet<ulong> packets = [];
        ulong high = Read(frame);
        _ = Read(frame);
        ulong count = Read(frame);
        ulong low = high - Read(frame);
        for (ulong pn = low; pn <= high; pn++)
        {
            _ = packets.Add(pn);
        }

        for (ulong i = 0; i < count; i++)
        {
            ulong gap = Read(frame);
            ulong length = Read(frame);
            high = low - gap - 2;
            low = high - length;
            for (ulong pn = low; pn <= high; pn++)
            {
                _ = packets.Add(pn);
            }
        }

        return packets;
    }

    [Fact]
    public void NewReno_WhenTheApplicationSendsLessThanHalfTheWindow_DoesNotGrowIt()
    {
        NewRenoController congestion = new(1200);
        long initial = congestion.Window;

        // One small packet per round trip, many times over: the window is never used.
        for (int i = 0; i < 1000; i++)
        {
            congestion.OnPacketSent(100);
            congestion.OnPacketAcked(100, timeSent: i);
        }

        Assert.Equal(initial, congestion.Window);
        Assert.Equal(0, congestion.BytesInFlight);
    }

    [Fact]
    public void NewReno_WhenTheWindowIsFilled_GrowsInSlowStart()
    {
        NewRenoController congestion = new(1200);
        long initial = congestion.Window;
        int packets = (int)(initial / 1200);

        for (int i = 0; i < packets; i++)
        {
            congestion.OnPacketSent(1200);
        }

        for (int i = 0; i < packets; i++)
        {
            congestion.OnPacketAcked(1200, timeSent: i);
        }

        // The first acknowledgements arrive with the window full and grow it; nothing is sent to refill the
        // flight, so the last ones find it under half the (now larger) window and stop the growth.
        Assert.Equal(initial + (4 * 1200), congestion.Window);
    }
}
