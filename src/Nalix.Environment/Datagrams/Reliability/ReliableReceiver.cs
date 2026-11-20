// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.IO;
using System;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;

namespace Nalix.Environment.Datagrams.Reliability;

/// <summary>
/// Receive side of the reliability layer: duplicate suppression, per-channel ordering and sequencing,
/// fragment reassembly with <see cref="FragmentAssembler"/>, and accounting of the bytes held back
/// against the advertised receive budgets.
/// </summary>
internal sealed class ReliableReceiver : IDisposable
{
    /// <summary>
    /// Cost added to every held message so that many tiny messages cannot exhaust memory within the
    /// byte budget. The sender counts the same cost.
    /// </summary>
    public const int PerMessageOverhead = 64;

    /// <summary>
    /// A <see cref="DeliveryMethod.ReliableUnordered"/> index must be below the oldest missing one plus this.
    /// </summary>
    public const ulong ReliableWindow = 1 << 16;

    private readonly int _channelCount;
    private readonly long _budget;
    private readonly long _channelBudget;
    private readonly int _maxMessageSize;
    private readonly long _fragmentTimeoutMs;
    private readonly ChannelState[] _channels;
    private readonly FragmentAssembler _assembler;
    private readonly Dictionary<ushort, AssemblyCost> _assemblyCosts = [];
    private readonly Dictionary<ushort, UnorderedFragments> _unorderedFragments = [];
    private readonly HashSet<ulong> _unorderedReceived = [];

    private ulong _unorderedBase;
    private long _held;

    public ReliableReceiver(DatagramEngineOptions options)
    {
        _channelCount = options.ChannelCount;
        _budget = options.ReceiveBufferBytes;
        _channelBudget = options.ChannelReceiveBufferBytes;
        _maxMessageSize = options.MaxMessageSize;
        _fragmentTimeoutMs = options.FragmentTimeoutMs;
        _channels = new ChannelState[options.ChannelCount];
        for (int i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new ChannelState();
        }

        _assembler = new FragmentAssembler
        {
            MaxStreamBytes = options.MaxMessageSize,
            MaxOpenStreams = options.MaxOpenFragmentStreams,
            StreamTimeoutMs = options.FragmentTimeoutMs,
        };
    }

    /// <summary>Gets the bytes currently held back (including <see cref="PerMessageOverhead"/>).</summary>
    public long HeldBytes => _held;

    /// <summary>
    /// Processes one MESSAGE frame. Delivered messages are appended to <paramref name="deliveries"/>.
    /// </summary>
    /// <returns><see cref="DatagramCloseCode.NoError"/>, or the code to close the session with.</returns>
    public DatagramCloseCode OnMessage(
        byte channel, DeliveryMethod method, ulong reliableIndex, ulong orderIndex, ReadOnlySpan<byte> payload,
        List<Delivery> deliveries)
    {
        if (channel >= _channelCount)
        {
            return DatagramCloseCode.ProtocolViolation;
        }

        ChannelState state = _channels[channel];

        switch (method)
        {
            case DeliveryMethod.Unreliable:
                return this.Deliver(payload, method, channel, deliveries);

            case DeliveryMethod.UnreliableSequenced:
                if (state.NewestUnreliableSequenced >= 0 && orderIndex <= (ulong)state.NewestUnreliableSequenced)
                {
                    return DatagramCloseCode.NoError; // Older than what was delivered.
                }

                state.NewestUnreliableSequenced = (long)orderIndex;
                return this.Deliver(payload, method, channel, deliveries);

            case DeliveryMethod.ReliableSequenced:
                if (state.NewestReliableSequenced >= 0 && orderIndex <= (ulong)state.NewestReliableSequenced)
                {
                    return DatagramCloseCode.NoError;
                }

                state.NewestReliableSequenced = (long)orderIndex;
                return this.Deliver(payload, method, channel, deliveries);

            case DeliveryMethod.ReliableUnordered:
                if (reliableIndex < _unorderedBase || _unorderedReceived.Contains(reliableIndex))
                {
                    return DatagramCloseCode.NoError; // Duplicate.
                }

                if (reliableIndex >= _unorderedBase + ReliableWindow)
                {
                    return DatagramCloseCode.FlowControlError;
                }

                _ = _unorderedReceived.Add(reliableIndex);
                while (_unorderedReceived.Remove(_unorderedBase))
                {
                    _unorderedBase++;
                }

                return FragmentAssembler.IsFragmentedFrame(payload, out FragmentHeader header)
                    ? this.OnUnorderedFragment(header, payload, channel, deliveries)
                    : this.Deliver(payload, method, channel, deliveries);

            case DeliveryMethod.ReliableOrdered:
                return this.OnOrdered(state, channel, orderIndex, payload, deliveries);

            default:
                return DatagramCloseCode.ProtocolViolation;
        }
    }

    /// <summary>Evicts incomplete fragmented messages that timed out and releases their held bytes.</summary>
    public void EvictExpired()
    {
        if (_assemblyCosts.Count == 0 && _unorderedFragments.Count == 0)
        {
            return;
        }

        long now = System.Environment.TickCount64;
        _ = _assembler.EvictExpired();

        List<ushort>? expired = null;
        foreach (KeyValuePair<ushort, AssemblyCost> entry in _assemblyCosts)
        {
            if (now - entry.Value.LastActivityMs > _fragmentTimeoutMs)
            {
                (expired ??= []).Add(entry.Key);
            }
        }

        foreach (KeyValuePair<ushort, UnorderedFragments> entry in _unorderedFragments)
        {
            if (now - entry.Value.LastActivityMs > _fragmentTimeoutMs)
            {
                (expired ??= []).Add(entry.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (ushort streamId in expired)
        {
            this.ReleaseAssembly(streamId);
            if (_unorderedFragments.Remove(streamId, out UnorderedFragments? fragments))
            {
                foreach (byte[] chunk in fragments.Chunks.Values)
                {
                    int length = BitConverter.ToInt32(chunk);
                    this.Release(fragments.Channel, length + PerMessageOverhead);
                    ClearAndReturn(chunk, length + sizeof(int));
                }
            }
        }
    }

    public void Dispose()
    {
        _assembler.Dispose();
        foreach (ChannelState state in _channels)
        {
            foreach (byte[] held in state.Held.Values)
            {
                BufferLease.ByteArrayPool.Return(held);
            }

            state.Held.Clear();
        }

        foreach (UnorderedFragments fragments in _unorderedFragments.Values)
        {
            foreach (byte[] chunk in fragments.Chunks.Values)
            {
                BufferLease.ByteArrayPool.Return(chunk);
            }
        }

        _unorderedFragments.Clear();
    }

    private DatagramCloseCode OnOrdered(ChannelState state, byte channel, ulong orderIndex, ReadOnlySpan<byte> payload, List<Delivery> deliveries)
    {
        if (orderIndex < state.NextOrdered || state.Held.ContainsKey(orderIndex))
        {
            return DatagramCloseCode.NoError; // Duplicate.
        }

        if (orderIndex > state.NextOrdered)
        {
            // Hold until the gap is filled. The length prefix keeps the payload size with the rented array.
            long cost = payload.Length + PerMessageOverhead;
            if (!this.TryHold(channel, cost))
            {
                return DatagramCloseCode.FlowControlError;
            }

            byte[] copy = BufferLease.ByteArrayPool.Rent(payload.Length + sizeof(int));
            _ = BitConverter.TryWriteBytes(copy, payload.Length);
            payload.CopyTo(copy.AsSpan(sizeof(int)));
            state.Held[orderIndex] = copy;
            return DatagramCloseCode.NoError;
        }

        DatagramCloseCode result = this.ProcessOrdered(payload, channel, deliveries);
        state.NextOrdered++;

        while (result == DatagramCloseCode.NoError && state.Held.Remove(state.NextOrdered, out byte[]? held))
        {
            int length = BitConverter.ToInt32(held);
            this.Release(channel, length + PerMessageOverhead);
            result = this.ProcessOrdered(held.AsSpan(sizeof(int), length), channel, deliveries);
            ClearAndReturn(held, length + sizeof(int));
            state.NextOrdered++;
        }

        return result;
    }

    private DatagramCloseCode ProcessOrdered(ReadOnlySpan<byte> payload, byte channel, List<Delivery> deliveries)
        => FragmentAssembler.IsFragmentedFrame(payload, out FragmentHeader header)
            ? this.Assemble(header, payload, channel, DeliveryMethod.ReliableOrdered, deliveries)
            : this.Deliver(payload, DeliveryMethod.ReliableOrdered, channel, deliveries);

    private DatagramCloseCode OnUnorderedFragment(FragmentHeader header, ReadOnlySpan<byte> payload, byte channel, List<Delivery> deliveries)
    {
        if (!_unorderedFragments.TryGetValue(header.StreamId, out UnorderedFragments? fragments))
        {
            fragments = new UnorderedFragments(channel);
            _unorderedFragments[header.StreamId] = fragments;
        }

        fragments.LastActivityMs = System.Environment.TickCount64;

        if (header.ChunkIndex != fragments.Next)
        {
            // FragmentAssembler needs chunks in index order: hold this one.
            if (header.ChunkIndex < fragments.Next || fragments.Chunks.ContainsKey(header.ChunkIndex))
            {
                return DatagramCloseCode.ProtocolViolation;
            }

            long cost = payload.Length + PerMessageOverhead;
            if (!this.TryHold(channel, cost))
            {
                return DatagramCloseCode.FlowControlError;
            }

            byte[] copy = BufferLease.ByteArrayPool.Rent(payload.Length + sizeof(int));
            _ = BitConverter.TryWriteBytes(copy, payload.Length);
            payload.CopyTo(copy.AsSpan(sizeof(int)));
            fragments.Chunks[header.ChunkIndex] = copy;
            return DatagramCloseCode.NoError;
        }

        DatagramCloseCode result = this.Assemble(header, payload, channel, DeliveryMethod.ReliableUnordered, deliveries);
        fragments.Next++;

        while (result == DatagramCloseCode.NoError && fragments.Chunks.Remove(fragments.Next, out byte[]? held))
        {
            int length = BitConverter.ToInt32(held);
            this.Release(channel, length + PerMessageOverhead);
            ReadOnlySpan<byte> chunk = held.AsSpan(sizeof(int), length);
            result = FragmentAssembler.IsFragmentedFrame(chunk, out FragmentHeader heldHeader)
                ? this.Assemble(heldHeader, chunk, channel, DeliveryMethod.ReliableUnordered, deliveries)
                : DatagramCloseCode.ProtocolViolation;
            ClearAndReturn(held, length + sizeof(int));
            fragments.Next++;
        }

        if (fragments.Next >= header.TotalChunks || result != DatagramCloseCode.NoError)
        {
            _ = _unorderedFragments.Remove(header.StreamId);
        }

        return result;
    }

    private DatagramCloseCode Assemble(FragmentHeader header, ReadOnlySpan<byte> payload, byte channel, DeliveryMethod method, List<Delivery> deliveries)
    {
        ReadOnlySpan<byte> body = payload[FragmentHeader.WireSize..];
        FragmentAssemblyResult? result;
        bool evicted;

        try
        {
            result = _assembler.Add(header, body, out evicted);
        }
        catch (InvalidDataException)
        {
            this.ReleaseAssembly(header.StreamId);
            return DatagramCloseCode.ProtocolViolation;
        }

        if (evicted)
        {
            // Over the per-message size limit or too many open streams.
            this.ReleaseAssembly(header.StreamId);
            return DatagramCloseCode.FlowControlError;
        }

        if (result is not FragmentAssemblyResult assembled)
        {
            long cost = body.Length + PerMessageOverhead;
            if (!this.TryHold(channel, cost))
            {
                return DatagramCloseCode.FlowControlError;
            }

            _assemblyCosts[header.StreamId] = _assemblyCosts.TryGetValue(header.StreamId, out AssemblyCost existing)
                ? existing with { Bytes = existing.Bytes + cost, LastActivityMs = System.Environment.TickCount64 }
                : new AssemblyCost(channel, cost, System.Environment.TickCount64);
            return DatagramCloseCode.NoError;
        }

        this.ReleaseAssembly(header.StreamId);
        assembled.Lease.ZeroOnDispose = true;
        deliveries.Add(new Delivery(assembled.Lease, method, channel));
        return DatagramCloseCode.NoError;
    }

    private DatagramCloseCode Deliver(ReadOnlySpan<byte> payload, DeliveryMethod method, byte channel, List<Delivery> deliveries)
    {
        if (payload.Length > _maxMessageSize)
        {
            return DatagramCloseCode.ProtocolViolation;
        }

#pragma warning disable CA2000 // Ownership passes to the delivery list and then to the handler.
        deliveries.Add(new Delivery(BufferLease.CopyFrom(payload, zeroOnDispose: true), method, channel));
#pragma warning restore CA2000
        return DatagramCloseCode.NoError;
    }

    private bool TryHold(byte channel, long cost)
    {
        ChannelState state = _channels[channel];
        if (state.HeldBytes + cost > _channelBudget || _held + cost > _budget)
        {
            return false;
        }

        state.HeldBytes += cost;
        _held += cost;
        return true;
    }

    private void Release(byte channel, long cost)
    {
        _channels[channel].HeldBytes -= cost;
        _held -= cost;
    }

    private void ReleaseAssembly(ushort streamId)
    {
        if (_assemblyCosts.Remove(streamId, out AssemblyCost cost))
        {
            this.Release(cost.Channel, cost.Bytes);
        }
    }

    private static void ClearAndReturn(byte[] buffer, int used)
    {
        buffer.AsSpan(0, used).Clear();
        BufferLease.ByteArrayPool.Return(buffer);
    }

    /// <summary>A message ready for the handler.</summary>
    internal readonly record struct Delivery(BufferLease Message, DeliveryMethod Method, byte Channel);

    private readonly record struct AssemblyCost(byte Channel, long Bytes, long LastActivityMs);

    private sealed class ChannelState
    {
        public ulong NextOrdered;
        public long NewestUnreliableSequenced = -1;
        public long NewestReliableSequenced = -1;
        public long HeldBytes;
        public readonly Dictionary<ulong, byte[]> Held = [];
    }

    private sealed class UnorderedFragments(byte channel)
    {
        public byte Channel { get; } = channel;

        public ushort Next;

        public long LastActivityMs = System.Environment.TickCount64;

        public readonly Dictionary<ushort, byte[]> Chunks = [];
    }
}
