// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Options;

namespace Nalix.Environment.Datagrams.Paths;

/// <summary>
/// Packetization-layer path MTU discovery (RFC 8899): starting at 1200 bytes, padded PING probes try the
/// candidates in increasing order; an acknowledged probe raises the MTU, three lost probes of a size end the
/// search, and repeated loss of full-size datagrams falls back to 1200 (black hole detection).
/// </summary>
internal sealed class MtuDiscovery
{
    private const int MaxProbeAttempts = 3;
    private const int BlackHoleThreshold = 3;

    private static readonly int[] s_candidates = [1232, 1350, 1400, 1452, 1472];

    private readonly bool _enabled;
    private int _index;
    private int _attempts;
    private bool _inFlight;
    private bool _requested;
    private int _consecutiveLargeLost;

    public MtuDiscovery(bool enabled) => _enabled = enabled;

    public int Mtu { get; private set; } = DatagramEngineOptions.MinimumMtu;

    /// <summary>Gets the time at which a probe is next wanted, or <see cref="long.MaxValue"/>.</summary>
    public long NextProbeTime { get; private set; } = long.MaxValue;

    /// <summary>Gets the size of the probe to send now, or 0 when none is wanted.</summary>
    public int PendingProbeSize => _requested && !_inFlight && _index < s_candidates.Length ? s_candidates[_index] : 0;

    /// <summary>Schedules the next probe one smoothed RTT from now, while candidates remain.</summary>
    public void Schedule(long now, long smoothedRtt)
        => this.NextProbeTime = _enabled && _index < s_candidates.Length ? now + smoothedRtt : long.MaxValue;

    /// <summary>
    /// The probe time passed: request a probe, or, without an RTT sample yet, ask for a PING to get one.
    /// </summary>
    /// <returns><see langword="true"/> when a PING should be sent first.</returns>
    public bool OnTimer(long now, bool hasRttSample, long probeTimeout)
    {
        if (now < this.NextProbeTime)
        {
            return false;
        }

        if (hasRttSample)
        {
            this.NextProbeTime = long.MaxValue;
            _requested = true;
            return false;
        }

        this.NextProbeTime = now + probeTimeout;
        return true;
    }

    public void OnProbeSent()
    {
        _inFlight = true;
        _requested = false;
    }

    public void OnProbeAcked(int size, long now, long smoothedRtt)
    {
        _inFlight = false;
        this.Mtu = size;
        _index++;
        _attempts = 0;
        this.Schedule(now, smoothedRtt);
    }

    public void OnProbeLost(long now, long smoothedRtt)
    {
        _inFlight = false;
        if (++_attempts >= MaxProbeAttempts)
        {
            this.Stop(); // This size does not pass: the search is complete.
            return;
        }

        this.Schedule(now, smoothedRtt);
    }

    public void OnLargePacketAcked() => _consecutiveLargeLost = 0;

    /// <summary>A datagram above the minimum MTU was lost.</summary>
    /// <returns><see langword="true"/> when the MTU fell back to the minimum.</returns>
    public bool OnLargePacketLost()
    {
        if (++_consecutiveLargeLost < BlackHoleThreshold)
        {
            return false;
        }

        _consecutiveLargeLost = 0;
        this.Mtu = DatagramEngineOptions.MinimumMtu;
        this.Stop();
        return true;
    }

    private void Stop()
    {
        _index = s_candidates.Length;
        _requested = false;
        this.NextProbeTime = long.MaxValue;
    }
}
