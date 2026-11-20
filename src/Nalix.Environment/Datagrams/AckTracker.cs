// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;

namespace Nalix.Environment.Datagrams;

/// <summary>
/// Received packet numbers to acknowledge, as ranges (largest first), and when the next ACK is due:
/// after every second ack-eliciting packet, when <c>max_ack_delay</c> expires, or immediately on a gap.
/// </summary>
internal sealed class AckTracker
{
    public const int MaxRanges = 32;

    // Ranges sorted by descending packet number, non-overlapping and non-adjacent.
    private readonly List<(ulong Low, ulong High)> _ranges = new(MaxRanges);
    private int _ackElicitingSinceAck;

    public long LargestReceivedTime { get; private set; }

    /// <summary>Gets the time an ACK must be sent at, or <see cref="long.MaxValue"/> when none is pending.</summary>
    public long AckDeadline { get; private set; } = long.MaxValue;

    /// <summary>Gets a value indicating whether there is anything to acknowledge.</summary>
    public bool HasRanges => _ranges.Count > 0;

    /// <summary>Gets a value indicating whether an ACK should go into the next datagram.</summary>
    public bool AckPending => this.AckDeadline != long.MaxValue;

    public void OnPacketReceived(ulong packetNumber, bool ackEliciting, long now, long maxAckDelay)
    {
        bool largest = _ranges.Count == 0 || packetNumber > _ranges[0].High;
        bool gap = _ranges.Count > 0 && (packetNumber != _ranges[0].High + 1);

        this.Insert(packetNumber);

        if (largest)
        {
            this.LargestReceivedTime = now;
        }

        if (!ackEliciting)
        {
            return;
        }

        _ackElicitingSinceAck++;
        if (gap || _ackElicitingSinceAck >= 2)
        {
            this.AckDeadline = now;
        }
        else if (this.AckDeadline == long.MaxValue)
        {
            this.AckDeadline = now + maxAckDelay;
        }
    }

    /// <summary>
    /// Writes an ACK frame (type byte included). Returns the bytes written, or 0 when nothing fits.
    /// </summary>
    public int WriteAckFrame(Span<byte> destination, long now, out ulong largestAcknowledged)
    {
        largestAcknowledged = 0;
        if (_ranges.Count == 0)
        {
            return 0;
        }

        largestAcknowledged = _ranges[0].High;
        ulong delay = (ulong)Math.Max(0, now - this.LargestReceivedTime) >> 3;

        // Drop the oldest ranges until the frame fits.
        for (int count = _ranges.Count; count > 0; count--)
        {
            int size = this.FrameSize(count, delay);
            if (size > destination.Length)
            {
                continue;
            }

            int offset = 0;
            destination[offset++] = (byte)FrameType.Ack;
            offset += VarInt.Write(destination[offset..], _ranges[0].High);
            offset += VarInt.Write(destination[offset..], delay);
            offset += VarInt.Write(destination[offset..], (ulong)(count - 1));
            offset += VarInt.Write(destination[offset..], _ranges[0].High - _ranges[0].Low);

            for (int i = 1; i < count; i++)
            {
                ulong gap = _ranges[i - 1].Low - _ranges[i].High - 2;
                offset += VarInt.Write(destination[offset..], gap);
                offset += VarInt.Write(destination[offset..], _ranges[i].High - _ranges[i].Low);
            }

            return offset;
        }

        return 0;
    }

    /// <summary>Called once an ACK frame went into a datagram.</summary>
    public void OnAckSent()
    {
        _ackElicitingSinceAck = 0;
        this.AckDeadline = long.MaxValue;
    }

    /// <summary>
    /// The peer received an ACK of ours covering everything up to <paramref name="largestAcknowledged"/>:
    /// those ranges need not be sent again.
    /// </summary>
    public void OnAckAcknowledged(ulong largestAcknowledged)
    {
        for (int i = _ranges.Count - 1; i >= 0; i--)
        {
            (ulong low, ulong high) = _ranges[i];
            if (high <= largestAcknowledged)
            {
                _ranges.RemoveAt(i);
            }
            else if (low <= largestAcknowledged)
            {
                _ranges[i] = (largestAcknowledged + 1, high);
            }
        }
    }

    private int FrameSize(int count, ulong delay)
    {
        int size = 1 + VarInt.SizeOf(_ranges[0].High) + VarInt.SizeOf(delay) + VarInt.SizeOf((ulong)(count - 1))
            + VarInt.SizeOf(_ranges[0].High - _ranges[0].Low);
        for (int i = 1; i < count; i++)
        {
            size += VarInt.SizeOf(_ranges[i - 1].Low - _ranges[i].High - 2) + VarInt.SizeOf(_ranges[i].High - _ranges[i].Low);
        }

        return size;
    }

    private void Insert(ulong pn)
    {
        int i = 0;
        while (i < _ranges.Count && _ranges[i].Low > pn + 1)
        {
            i++;
        }

        if (i == _ranges.Count)
        {
            // Older than every range.
            if (_ranges.Count < MaxRanges)
            {
                _ranges.Add((pn, pn));
            }

            return;
        }

        (ulong low, ulong high) = _ranges[i];
        if (pn >= low && pn <= high)
        {
            return; // Already recorded.
        }

        if (pn == high + 1)
        {
            _ranges[i] = (low, pn);
            // Merging with the newer range above is impossible: ranges are non-adjacent and that range's Low > pn + 1.
            return;
        }

        if (pn + 1 == low)
        {
            _ranges[i] = (pn, high);
            if (i + 1 < _ranges.Count && _ranges[i + 1].High + 1 == pn)
            {
                _ranges[i] = (_ranges[i + 1].Low, high);
                _ranges.RemoveAt(i + 1);
            }

            return;
        }

        // pn > high + 1: a new range above range i.
        _ranges.Insert(i, (pn, pn));
        if (_ranges.Count > MaxRanges)
        {
            _ranges.RemoveAt(_ranges.Count - 1);
        }
    }
}
