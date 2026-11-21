// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Environment.Datagrams.Congestion;

/// <summary>
/// Pacer of RFC 9002 §7.7: sends at <c>1.25 · cwnd / srtt</c> with a burst of up to 10 datagrams.
/// </summary>
internal sealed class Pacer
{
    private double _tokens;
    private long _lastUpdate = long.MinValue;

    /// <summary>
    /// Returns the time at which a datagram of <paramref name="size"/> bytes may be sent; <paramref name="now"/>
    /// or earlier means immediately.
    /// </summary>
    public long NextSendTime(int size, long window, long smoothedRtt, int mtu, long now)
    {
        this.Refill(window, smoothedRtt, mtu, now);
        if (_tokens >= size)
        {
            return now;
        }

        double rate = Rate(window, smoothedRtt);
        return now + (long)Math.Ceiling((size - _tokens) / rate);
    }

    public void OnSent(int size) => _tokens -= size;

    private void Refill(long window, long smoothedRtt, int mtu, long now)
    {
        double capacity = Math.Min(window, 10L * mtu);
        if (_lastUpdate == long.MinValue)
        {
            _tokens = capacity;
        }
        else if (now > _lastUpdate)
        {
            _tokens = Math.Min(capacity, _tokens + ((now - _lastUpdate) * Rate(window, smoothedRtt)));
        }

        _lastUpdate = now;
    }

    /// <summary>Bytes per microsecond.</summary>
    private static double Rate(long window, long smoothedRtt) => 1.25 * window / Math.Max(smoothedRtt, 1);
}
