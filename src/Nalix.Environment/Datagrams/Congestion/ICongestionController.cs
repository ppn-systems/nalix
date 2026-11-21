// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Environment.Datagrams.Congestion;

/// <summary>
/// Congestion controller contract, so CUBIC or BBR can be added later.
/// </summary>
internal interface ICongestionController
{
    long Window { get; }

    long BytesInFlight { get; }

    void OnPacketSent(int size);

    void OnPacketAcked(int size, long timeSent);

    void OnPacketsLost(long lostBytes, long largestLostTimeSent, bool persistentCongestion, long now);

    /// <summary>Removes bytes of a packet that is no longer tracked without signalling congestion (an MTU probe).</summary>
    void OnPacketDiscarded(int size);

    void OnMtuChanged(int mtu);

    void Reset();
}
