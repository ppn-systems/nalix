// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams.Wire;
using Nalix.Environment.Fragments;

namespace Nalix.Environment.Datagrams.Reliability;

/// <summary>
/// One MESSAGE frame to send: a whole message, or one chunk of a fragmented one.
/// </summary>
internal sealed class OutMessage
{
    public byte[] Buffer = [];
    public int Length;
    public DeliveryMethod Method;
    public byte Channel;
    public byte Priority;
    public ulong ReliableIndex;
    public bool HasReliableIndex;
    public ulong OrderIndex;
    public long EnqueueTime;
    public MessageGroup? Group;

    /// <summary>The message was sent at least once (its queue bytes were released).</summary>
    public bool SentOnce;

    /// <summary>Whether this is one chunk of a fragmented message; its body is in <see cref="MessageGroup.Buffer"/>.</summary>
    public bool IsChunk;

    /// <summary>The chunk's fragment header, written in front of its body.</summary>
    public FragmentHeader Chunk;

    /// <summary>Where the chunk's body starts in <see cref="MessageGroup.Buffer"/>.</summary>
    public int ChunkOffset;

    /// <summary>The neighbours in the <see cref="OutMessageList"/> holding this message, if any.</summary>
    public OutMessage? Previous;

    /// <inheritdoc cref="Previous"/>
    public OutMessage? Next;

    public bool Acked;

    public int FrameSize
    {
        get
        {
            int size = 3 + VarInt.SizeOf((ulong)Length) + Length;
            if (Method == DeliveryMethod.ReliableUnordered)
            {
                size += VarInt.SizeOf(ReliableIndex);
            }

            if (Method.HasOrderIndex())
            {
                size += VarInt.SizeOf(OrderIndex);
            }

            return size;
        }
    }
}

/// <summary>
/// A logical message: the unit of flow-control release and of the ACK receipt.
/// </summary>
internal sealed class MessageGroup
{
    public int Remaining;
    public long Cost;
    public byte Channel;
    public bool Ordered;
    public bool Charged;
    public bool Released;
    public bool Superseded;
    public TaskCompletionSource<bool>? Receipt;

    /// <summary>The whole message of a fragmented group, shared by its chunks; empty otherwise.</summary>
    public byte[] Buffer = [];

    /// <summary>The message length in <see cref="Buffer"/>.</summary>
    public int BufferLength;

    /// <summary>The chunks not yet returned; the last one returns <see cref="Buffer"/> to the pool.</summary>
    public int BufferUsers;
}

/// <summary>
/// Doubly linked list threaded through <see cref="OutMessage.Previous"/> and <see cref="OutMessage.Next"/>: queuing
/// a message allocates nothing (a <see cref="System.Collections.Generic.LinkedList{T}"/> allocates a node per add).
/// A message is in at most one list at a time.
/// </summary>
internal sealed class OutMessageList
{
    private OutMessage? _last;

    /// <summary>Gets the first message, or <see langword="null"/> when the list is empty.</summary>
    public OutMessage? First { get; private set; }

    public void AddLast(OutMessage m)
    {
        m.Previous = _last;
        m.Next = null;
        if (_last is null)
        {
            this.First = m;
        }
        else
        {
            _last.Next = m;
        }

        _last = m;
    }

    public void Remove(OutMessage m)
    {
        if (m.Previous is null)
        {
            this.First = m.Next;
        }
        else
        {
            m.Previous.Next = m.Next;
        }

        if (m.Next is null)
        {
            _last = m.Previous;
        }
        else
        {
            m.Next.Previous = m.Previous;
        }

        m.Previous = null;
        m.Next = null;
    }
}
