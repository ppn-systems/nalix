// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using Nalix.Environment.Datagrams.Reliability;

namespace Nalix.Environment.Datagrams.Recovery;

/// <summary>
/// A sent datagram awaiting acknowledgement.
/// </summary>
internal sealed class SentPacket
{
    public ulong PacketNumber;
    public long TimeSent;
    public int Size;
    public bool AckEliciting;
    public bool InFlight;
    public bool IsMtuProbe;
    public bool KeyPhase;
    public long LargestAckSent = -1;
    public readonly List<OutMessage> Messages = [];

    public void Reset()
    {
        Messages.Clear();
        LargestAckSent = -1;
        IsMtuProbe = false;
        AckEliciting = false;
        InFlight = false;
    }
}
