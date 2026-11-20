// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams.Wire;

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
}
