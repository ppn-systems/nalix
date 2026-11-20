// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;

namespace Nalix.Environment.Datagrams;

/// <summary>
/// Sent packets awaiting acknowledgement and the loss detection of RFC 9002 §6: a packet is lost when one
/// sent 3 packet numbers later was acknowledged, or when it is older than <c>9/8 · max(srtt, latest_rtt)</c>.
/// </summary>
internal sealed class LossRecovery
{
    public const int PacketThreshold = 3;

    // Sorted by packet number, as sent.
    private readonly List<SentPacket> _sent = [];

    /// <summary>Gets the largest packet number the peer acknowledged, or -1.</summary>
    public long LargestAcked { get; private set; } = -1;

    /// <summary>Gets the time the next packet becomes lost by the time threshold, or <see cref="long.MaxValue"/>.</summary>
    public long LossTime { get; private set; } = long.MaxValue;

    public bool HasAckElicitingInFlight
    {
        get
        {
            foreach (SentPacket p in _sent)
            {
                if (p.AckEliciting && p.InFlight)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Add(SentPacket packet) => _sent.Add(packet);

    public void OnLargestAcked(ulong largest)
    {
        if ((long)largest > this.LargestAcked)
        {
            this.LargestAcked = (long)largest;
        }
    }

    /// <summary>
    /// Removes the packets in <c>[low, high]</c> and inserts them at the front of <paramref name="acked"/>; ranges
    /// come from the largest down, so <paramref name="acked"/> stays sorted ascending.
    /// </summary>
    public void CollectAcked(ulong low, ulong high, List<SentPacket> acked)
    {
        int lo = 0;
        int hi = _sent.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_sent[mid].PacketNumber < low)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        int end = lo;
        while (end < _sent.Count && _sent[end].PacketNumber <= high)
        {
            end++;
        }

        if (end > lo)
        {
            acked.InsertRange(0, _sent.GetRange(lo, end - lo));
            _sent.RemoveRange(lo, end - lo);
        }
    }

    /// <summary>
    /// Removes the lost packets into <paramref name="lost"/> and updates <see cref="LossTime"/>.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="rtt">The RTT estimator.</param>
    /// <param name="lost">Receives the lost packets, ascending.</param>
    /// <param name="contiguous">Whether no packet between the first and last lost one survived.</param>
    public void DetectLost(long now, RttEstimator rtt, List<SentPacket> lost, out bool contiguous)
    {
        this.LossTime = long.MaxValue;
        contiguous = true;
        if (this.LargestAcked < 0)
        {
            return;
        }

        long lossDelay = Math.Max(9 * Math.Max(rtt.Smoothed, rtt.Latest) / 8, RttEstimator.Granularity);
        long lostSendTime = now - lossDelay;
        int lastLost = -1;
        int kept = 0;

        for (int i = 0; i < _sent.Count; i++)
        {
            SentPacket p = _sent[i];
            bool isLost = p.PacketNumber <= (ulong)this.LargestAcked &&
                (p.TimeSent <= lostSendTime || (ulong)this.LargestAcked >= p.PacketNumber + PacketThreshold);

            if (!isLost)
            {
                if (p.PacketNumber <= (ulong)this.LargestAcked)
                {
                    this.LossTime = Math.Min(this.LossTime, p.TimeSent + lossDelay);
                }

                _sent[kept++] = p;
                continue;
            }

            if (lastLost >= 0 && i != lastLost + 1)
            {
                contiguous = false;
            }

            lastLost = i;
            lost.Add(p);
        }

        _sent.RemoveRange(kept, _sent.Count - kept);
    }

    /// <summary>Removes every tracked packet into <paramref name="packets"/>.</summary>
    public void TakeAll(List<SentPacket> packets)
    {
        packets.AddRange(_sent);
        _sent.Clear();
    }

    /// <summary>
    /// Decodes an ACK frame (after its type byte), moves the newly acknowledged packets into
    /// <paramref name="acked"/> in ascending order and takes an RTT sample (RFC 9002 §5.1).
    /// </summary>
    public DatagramCloseCode OnAckFrame(
        ReadOnlySpan<byte> payload, ref int offset, long now, ulong nextPacketNumber,
        RttEstimator rtt, long maxAckDelay, List<SentPacket> acked)
    {
        if (!VarInt.TryRead(payload, ref offset, out ulong largest) ||
            !VarInt.TryRead(payload, ref offset, out ulong ackDelay) ||
            !VarInt.TryRead(payload, ref offset, out ulong rangeCount) ||
            !VarInt.TryRead(payload, ref offset, out ulong firstRange) ||
            firstRange > largest || rangeCount > AckTracker.MaxRanges * 4)
        {
            return DatagramCloseCode.FrameEncodingError;
        }

        if (largest >= nextPacketNumber)
        {
            return DatagramCloseCode.ProtocolViolation; // Acknowledges a packet never sent.
        }

        ulong low = largest - firstRange;
        this.CollectAcked(low, largest, acked);

        for (ulong i = 0; i < rangeCount; i++)
        {
            if (!VarInt.TryRead(payload, ref offset, out ulong gap) || !VarInt.TryRead(payload, ref offset, out ulong length) ||
                gap + 2 > low || length > low - gap - 2)
            {
                return DatagramCloseCode.FrameEncodingError;
            }

            ulong high = low - gap - 2;
            low = high - length;
            this.CollectAcked(low, high, acked);
        }

        if (acked.Count == 0)
        {
            return DatagramCloseCode.NoError;
        }

        this.OnLargestAcked(largest);

        // RTT sample when the largest acknowledged is newly acknowledged and something ack-eliciting was.
        SentPacket newest = acked[^1];
        if (newest.PacketNumber == largest)
        {
            foreach (SentPacket p in acked)
            {
                if (p.AckEliciting)
                {
                    rtt.Update(now - newest.TimeSent, (long)Math.Min(ackDelay << 3, long.MaxValue / 2), maxAckDelay);
                    break;
                }
            }
        }

        return DatagramCloseCode.NoError;
    }

    /// <summary>
    /// Gets the time of the loss timer or, without one, of the probe timeout with exponential backoff.
    /// </summary>
    public long NextTimeout(RttEstimator rtt, long maxAckDelay, int ptoCount, long lastAckElicitingSent)
    {
        if (this.LossTime != long.MaxValue)
        {
            return this.LossTime;
        }

        return this.HasAckElicitingInFlight
            ? lastAckElicitingSent + (rtt.ProbeTimeout(maxAckDelay) << Math.Min(ptoCount, 16))
            : long.MaxValue;
    }

    /// <summary>
    /// Persistent congestion (RFC 9002 §7.6): ack-eliciting packets lost over a span longer than three probe
    /// timeouts, with an RTT sample available. The caller checks that no packet in between was acknowledged.
    /// </summary>
    public static bool IsPersistentCongestion(List<SentPacket> lost, RttEstimator rtt, long maxAckDelay)
    {
        if (!rtt.HasSample || lost.Count < 2)
        {
            return false;
        }

        long first = long.MaxValue;
        long last = long.MinValue;
        foreach (SentPacket p in lost)
        {
            if (p.AckEliciting && p.InFlight)
            {
                first = Math.Min(first, p.TimeSent);
                last = Math.Max(last, p.TimeSent);
            }
        }

        return first != long.MaxValue && last - first >= 3 * rtt.ProbeTimeout(maxAckDelay);
    }
}
