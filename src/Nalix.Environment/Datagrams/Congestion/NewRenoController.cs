// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Environment.Datagrams.Congestion;

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
