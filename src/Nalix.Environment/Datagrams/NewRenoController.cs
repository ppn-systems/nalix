// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Environment.Datagrams;

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

/// <summary>
/// NewReno as in RFC 9002 §7 and Appendix B: slow start, congestion avoidance, one reduction per
/// recovery period, and persistent congestion.
/// </summary>
internal sealed class NewRenoController : ICongestionController
{
    private int _mtu;
    private long _ssthresh = long.MaxValue;
    private long _recoveryStart = long.MinValue;
    private long _ackedInAvoidance;

    public NewRenoController(int mtu)
    {
        _mtu = mtu;
        this.Window = InitialWindow(mtu);
    }

    public long Window { get; private set; }

    public long BytesInFlight { get; private set; }

    private long MinimumWindow => 2L * _mtu;

    public void OnPacketSent(int size) => this.BytesInFlight += size;

    public void OnPacketAcked(int size, long timeSent)
    {
        this.BytesInFlight = Math.Max(0, this.BytesInFlight - size);

        if (timeSent <= _recoveryStart)
        {
            return; // Acknowledgements of packets sent before the recovery period do not grow the window.
        }

        if (this.Window < _ssthresh)
        {
            this.Window += size;
            return;
        }

        _ackedInAvoidance += size;
        if (_ackedInAvoidance >= this.Window)
        {
            _ackedInAvoidance -= this.Window;
            this.Window += _mtu;
        }
    }

    public void OnPacketsLost(long lostBytes, long largestLostTimeSent, bool persistentCongestion, long now)
    {
        this.BytesInFlight = Math.Max(0, this.BytesInFlight - lostBytes);

        if (largestLostTimeSent > _recoveryStart)
        {
            _recoveryStart = now;
            _ssthresh = Math.Max(this.Window / 2, this.MinimumWindow);
            this.Window = _ssthresh;
            _ackedInAvoidance = 0;
        }

        if (persistentCongestion)
        {
            this.Window = this.MinimumWindow;
            _recoveryStart = long.MinValue;
        }
    }

    public void OnPacketDiscarded(int size) => this.BytesInFlight = Math.Max(0, this.BytesInFlight - size);

    public void OnMtuChanged(int mtu)
    {
        _mtu = mtu;
        this.Window = Math.Max(this.Window, this.MinimumWindow);
    }

    public void Reset()
    {
        this.Window = InitialWindow(_mtu);
        _ssthresh = long.MaxValue;
        _recoveryStart = long.MinValue;
        _ackedInAvoidance = 0;
    }

    private static long InitialWindow(int mtu) => Math.Min(10L * mtu, Math.Max(14_720L, 2L * mtu));
}

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
