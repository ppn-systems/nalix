// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams.Wire;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;

namespace Nalix.Environment.Datagrams.Reliability;

/// <summary>
/// Send side of the reliability layer: per-priority queues, retransmission queue, order counters,
/// fragmentation, the advertised flow-control budgets and ACK receipts.
/// </summary>
internal sealed class ReliableSender
{
    private readonly DatagramEngineOptions _options;
    private readonly OutMessageList _retransmit = new();
    private readonly OutMessageList[] _queues = new OutMessageList[DatagramEngine.PriorityLevels];
    private readonly long[] _queuedBytes;
    private readonly long[] _unreleasedBytes;
    private readonly ulong[] _nextUnreliableSequenced;
    private readonly ulong[] _nextReliableOrdered;
    private readonly ulong[] _nextReliableSequenced;
    private readonly Queue<MessageGroup>[] _orderedGroups;
    private readonly bool[] _blocked;
    private readonly ulong[] _unackedReliable = new ulong[(int)(ReliableReceiver.ReliableWindow / 64)];
    private readonly Stack<OutMessage> _messagePool = new();
    private readonly Stack<MessageGroup> _groupPool = new();

    private long _unreleasedTotal;
    private ulong _nextReliableIndex;
    private ulong _oldestUnackedReliable;
    private DatagramPeerLimits _peer;
    private bool _closed;

    public ReliableSender(DatagramEngineOptions options, DatagramPeerLimits peer)
    {
        _options = options;
        _peer = peer;
        int channels = options.ChannelCount;
        _queuedBytes = new long[channels];
        _unreleasedBytes = new long[channels];
        _nextUnreliableSequenced = new ulong[channels];
        _nextReliableOrdered = new ulong[channels];
        _nextReliableSequenced = new ulong[channels];
        _orderedGroups = new Queue<MessageGroup>[channels];
        _blocked = new bool[channels];

        for (int i = 0; i < channels; i++)
        {
            _orderedGroups[i] = new Queue<MessageGroup>();
        }

        for (int i = 0; i < DatagramEngine.PriorityLevels; i++)
        {
            _queues[i] = new OutMessageList();
        }
    }

    /// <summary>Gets a value indicating whether a write may find something to send.</summary>
    public bool MayHaveSendable { get; private set; }

    public long UnreleasedBytes => _unreleasedTotal;

    public DatagramPeerLimits PeerLimits => _peer;

    public void RaisePeerLimits(DatagramPeerLimits limits)
    {
        _peer = new DatagramPeerLimits(
            Math.Max(_peer.ReceiveBufferBytes, limits.ReceiveBufferBytes),
            Math.Max(_peer.ChannelReceiveBufferBytes, limits.ChannelReceiveBufferBytes));
        this.MayHaveSendable = true;
    }

    public bool TryEnqueue(ReadOnlySpan<byte> message, DeliveryMethod method, byte channel, byte priority, long now, TaskCompletionSource<bool>? receipt)
    {
        if (_closed || message.Length > _options.MaxMessageSize || FragmentAssembler.IsFragmentedFrame(message, out _))
        {
            return false;
        }

        bool reliable = method.IsReliable();
        int chunks = message.Length <= DatagramEngine.MaxUnfragmentedMessageSize ? 1 : (message.Length + DatagramEngine.ChunkBodySize - 1) / DatagramEngine.ChunkBodySize;

        if (chunks > 1 && method is not (DeliveryMethod.ReliableOrdered or DeliveryMethod.ReliableUnordered))
        {
            return false; // Only reliable methods may fragment.
        }

        if (chunks > ushort.MaxValue || _queuedBytes[channel] + message.Length > _options.SendQueueBytesPerChannel)
        {
            return false;
        }

        MessageGroup? group = null;
        if (reliable)
        {
            long cost = message.Length + ((long)chunks * (FragmentHeader.WireSize + ReliableReceiver.PerMessageOverhead));
            if (cost > _peer.ChannelReceiveBufferBytes || cost > _peer.ReceiveBufferBytes)
            {
                return false; // Could never be released by this peer.
            }

            group = _groupPool.Count > 0 ? _groupPool.Pop() : new MessageGroup();
            group.Remaining = chunks;
            group.Cost = cost;
            group.Channel = channel;
            group.Ordered = method == DeliveryMethod.ReliableOrdered;
            group.Released = false;
            group.Charged = false;
            group.Superseded = false;
            group.Receipt = receipt;

            if (group.Ordered)
            {
                _orderedGroups[channel].Enqueue(group);
            }
        }

        OutMessageList queue = _queues[Math.Min(priority, (byte)(DatagramEngine.PriorityLevels - 1))];
        ushort streamId = chunks > 1 ? FragmentStreamId.Next() : (ushort)0;

        for (int i = 0; i < chunks; i++)
        {
            OutMessage m = this.RentMessage();
            m.Method = method;
            m.Channel = channel;
            m.Priority = priority;
            m.EnqueueTime = now;
            m.Group = group;

            if (chunks == 1)
            {
                m.Buffer = BufferLease.ByteArrayPool.Rent(Math.Max(message.Length, 1));
                message.CopyTo(m.Buffer);
                m.Length = message.Length;
            }
            else
            {
                // One copy of the whole message, shared by its chunks: one pool rent instead of one per chunk.
                // The fragment header is written with the frame (WriteFrame).
                if (i == 0)
                {
                    group!.Buffer = BufferLease.ByteArrayPool.Rent(message.Length);
                    message.CopyTo(group.Buffer);
                    group.BufferLength = message.Length;
                    group.BufferUsers = chunks;
                }

                int offset = i * DatagramEngine.ChunkBodySize;
                int length = Math.Min(DatagramEngine.ChunkBodySize, message.Length - offset);
                m.IsChunk = true;
                m.Chunk = new FragmentHeader(streamId, (ushort)i, (ushort)chunks, i == chunks - 1);
                m.ChunkOffset = offset;
                m.Length = FragmentHeader.WireSize + length;
            }

            m.OrderIndex = method switch
            {
                DeliveryMethod.UnreliableSequenced => _nextUnreliableSequenced[channel]++,
                DeliveryMethod.ReliableOrdered => _nextReliableOrdered[channel]++,
                DeliveryMethod.ReliableSequenced => _nextReliableSequenced[channel]++,
                DeliveryMethod.Unreliable or DeliveryMethod.ReliableUnordered => 0,
                _ => 0,
            };

            _queuedBytes[channel] += m.Length;
            queue.AddLast(m);
        }

        this.MayHaveSendable = true;
        return true;
    }

    /// <summary>
    /// Writes MESSAGE frames into <paramref name="destination"/> until it is full. Reliable frames are added to
    /// <paramref name="carried"/>; unreliable ones are released once written.
    /// </summary>
    /// <returns><see langword="true"/> when at least one frame was written.</returns>
    public bool WriteMessages(Span<byte> destination, long now, List<OutMessage> carried, out int written)
    {
        int offset = 0;
        bool full = false;

        // Retransmissions go first.
        for (OutMessage? m = _retransmit.First; m is not null && !full;)
        {
            OutMessage? next = m.Next;

            if (m.Method == DeliveryMethod.ReliableSequenced && m.OrderIndex + 1 < _nextReliableSequenced[m.Channel])
            {
                _retransmit.Remove(m);
                this.Supersede(m);
            }
            else if (m.FrameSize <= destination.Length - offset)
            {
                _retransmit.Remove(m);
                offset += WriteFrame(destination[offset..], m);
                carried.Add(m);
            }
            else
            {
                full = true;
            }

            m = next;
        }

        Array.Clear(_blocked);
        bool windowBlocked = false;

        for (int level = DatagramEngine.PriorityLevels - 1; level >= 0 && !full; level--)
        {
            OutMessageList queue = _queues[level];
            for (OutMessage? m = queue.First; m is not null && !full;)
            {
                OutMessage? next = m.Next;

                if (!m.Method.IsReliable())
                {
                    if (now - m.EnqueueTime > _options.UnreliableExpiryMs * 1000L)
                    {
                        // Too old to be useful: drop instead of sending late.
                        queue.Remove(m);
                        _queuedBytes[m.Channel] -= m.Length;
                        this.ReturnMessage(m);
                        m = next;
                        continue;
                    }
                }
                else if (_blocked[m.Channel] || !this.TryCharge(m, ref windowBlocked))
                {
                    _blocked[m.Channel] = true;
                    m = next;
                    continue;
                }

                if (m.FrameSize > destination.Length - offset)
                {
                    full = true;
                    break;
                }

                queue.Remove(m);
                _queuedBytes[m.Channel] -= m.Length;
                m.SentOnce = true;
                offset += WriteFrame(destination[offset..], m);

                if (m.Method.IsReliable())
                {
                    carried.Add(m);
                }
                else
                {
                    this.ReturnMessage(m);
                }

                m = next;
            }
        }

        // When everything left is blocked by flow control, wait for an acknowledgement or a new send.
        if (!full)
        {
            this.MayHaveSendable = false;
        }

        written = offset;
        return offset > 0;
    }

    public void OnAcked(OutMessage m)
    {
        if (m.Acked)
        {
            return;
        }

        m.Acked = true;
        if (m.HasReliableIndex)
        {
            this.ClearUnacked(m.ReliableIndex);
        }

        MessageGroup? group = m.Group;
        this.ReturnMessage(m);

        if (group is null || --group.Remaining > 0)
        {
            return;
        }

        _ = group.Receipt?.TrySetResult(true);
        group.Receipt = null;

        if (group.Ordered)
        {
            this.ReleaseOrderedHead(group.Channel);
        }
        else
        {
            this.Release(group);
            _groupPool.Push(group);
        }
    }

    public void OnLost(OutMessage m)
    {
        if (m.Acked)
        {
            return;
        }

        if (m.Method == DeliveryMethod.ReliableSequenced && m.OrderIndex + 1 < _nextReliableSequenced[m.Channel])
        {
            this.Supersede(m);
            return;
        }

        _retransmit.AddLast(m);
        this.MayHaveSendable = true;
    }

    /// <summary>Fails pending receipts and returns every buffer.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        this.FailAll(_retransmit);
        foreach (OutMessageList queue in _queues)
        {
            this.FailAll(queue);
        }

        foreach (Queue<MessageGroup> groups in _orderedGroups)
        {
            foreach (MessageGroup group in groups)
            {
                _ = group.Receipt?.TrySetResult(false);
                group.Receipt = null;
            }

            groups.Clear();
        }

        Array.Clear(_queuedBytes);
        this.MayHaveSendable = false;
    }

    private void FailAll(OutMessageList list)
    {
        while (list.First is { } m)
        {
            list.Remove(m);
            this.Fail(m);
        }
    }

    /// <summary>A message that was in flight when the session closed.</summary>
    public void Fail(OutMessage m)
    {
        _ = m.Group?.Receipt?.TrySetResult(false);
        m.Group?.Receipt = null;

        this.ReturnMessage(m);
    }

    private bool TryCharge(OutMessage m, ref bool windowBlocked)
    {
        MessageGroup group = m.Group!;

        if (m.Method == DeliveryMethod.ReliableUnordered && !m.HasReliableIndex)
        {
            if (windowBlocked || _nextReliableIndex - _oldestUnackedReliable >= ReliableReceiver.ReliableWindow)
            {
                windowBlocked = true;
                return false;
            }

            m.ReliableIndex = _nextReliableIndex++;
            m.HasReliableIndex = true;
            this.SetUnacked(m.ReliableIndex);
        }

        // The group's cost is charged when its first frame is sent.
        if (!group.Charged)
        {
            if (_unreleasedTotal + group.Cost > _peer.ReceiveBufferBytes ||
                _unreleasedBytes[group.Channel] + group.Cost > _peer.ChannelReceiveBufferBytes)
            {
                return false;
            }

            _unreleasedTotal += group.Cost;
            _unreleasedBytes[group.Channel] += group.Cost;
            group.Charged = true;
        }

        return true;
    }

    private void ReleaseOrderedHead(byte channel)
    {
        Queue<MessageGroup> groups = _orderedGroups[channel];
        while (groups.TryPeek(out MessageGroup? head) && head.Remaining == 0)
        {
            _ = groups.Dequeue();
            this.Release(head);
            _groupPool.Push(head);
        }
    }

    private void Supersede(OutMessage m)
    {
        MessageGroup? group = m.Group;
        this.ReturnMessage(m);

        if (group is not null)
        {
            group.Superseded = true;
            _ = group.Receipt?.TrySetResult(false);
            group.Receipt = null;
            this.Release(group);
            _groupPool.Push(group);
        }
    }

    private void Release(MessageGroup group)
    {
        if (group.Charged && !group.Released)
        {
            _unreleasedTotal -= group.Cost;
            _unreleasedBytes[group.Channel] -= group.Cost;
        }

        group.Released = true;
        this.MayHaveSendable = true;
    }

    private void SetUnacked(ulong index) => _unackedReliable[(int)(index % ReliableReceiver.ReliableWindow) >> 6] |= 1UL << (int)(index & 63);

    private void ClearUnacked(ulong index)
    {
        _unackedReliable[(int)(index % ReliableReceiver.ReliableWindow) >> 6] &= ~(1UL << (int)(index & 63));
        while (_oldestUnackedReliable < _nextReliableIndex &&
               (_unackedReliable[(int)(_oldestUnackedReliable % ReliableReceiver.ReliableWindow) >> 6] & (1UL << (int)(_oldestUnackedReliable & 63))) == 0)
        {
            _oldestUnackedReliable++;
        }
    }

    private OutMessage RentMessage()
    {
        OutMessage m = _messagePool.Count > 0 ? _messagePool.Pop() : new OutMessage();
        m.Acked = false;
        m.SentOnce = false;
        m.HasReliableIndex = false;
        m.ReliableIndex = 0;
        m.OrderIndex = 0;
        return m;
    }

    private void ReturnMessage(OutMessage m)
    {
        if (m.IsChunk)
        {
            // The last chunk returned releases the message the chunks share.
            MessageGroup group = m.Group!;
            if (--group.BufferUsers == 0)
            {
                group.Buffer.AsSpan(0, group.BufferLength).Clear();
                BufferLease.ByteArrayPool.Return(group.Buffer);
                group.Buffer = [];
                group.BufferLength = 0;
            }

            m.IsChunk = false;
            m.ChunkOffset = 0;
        }
        else if (m.Buffer.Length > 0)
        {
            m.Buffer.AsSpan(0, m.Length).Clear();
            BufferLease.ByteArrayPool.Return(m.Buffer);
        }

        m.Buffer = [];
        m.Length = 0;
        m.Group = null;
        _messagePool.Push(m);
    }

    private static int WriteFrame(Span<byte> destination, OutMessage m)
    {
        int offset = 0;
        destination[offset++] = (byte)FrameType.Message;
        destination[offset++] = m.Channel;
        destination[offset++] = (byte)m.Method;

        if (m.Method == DeliveryMethod.ReliableUnordered)
        {
            offset += VarInt.Write(destination[offset..], m.ReliableIndex);
        }

        if (m.Method.HasOrderIndex())
        {
            offset += VarInt.Write(destination[offset..], m.OrderIndex);
        }

        offset += VarInt.Write(destination[offset..], (ulong)m.Length);
        if (m.IsChunk)
        {
            m.Chunk.WriteTo(destination[offset..]);
            m.Group!.Buffer.AsSpan(m.ChunkOffset, m.Length - FragmentHeader.WireSize).CopyTo(destination[(offset + FragmentHeader.WireSize)..]);
        }
        else
        {
            m.Buffer.AsSpan(0, m.Length).CopyTo(destination[offset..]);
        }

        return offset + m.Length;
    }
}
