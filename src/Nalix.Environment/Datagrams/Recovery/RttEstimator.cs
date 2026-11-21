// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Environment.Datagrams.Recovery;

/// <summary>
/// RTT estimation of RFC 9002 §5, in microseconds.
/// </summary>
internal sealed class RttEstimator
{
    public const long InitialRtt = 333_000;
    public const long Granularity = 1_000;

    public long Latest { get; private set; }

    public long Smoothed { get; private set; } = InitialRtt;

    public long Variance { get; private set; } = InitialRtt / 2;

    public long Min { get; private set; } = long.MaxValue;

    public bool HasSample { get; private set; }

    public void Update(long latest, long ackDelay, long maxAckDelay)
    {
        latest = Math.Max(latest, 1);
        this.Latest = latest;

        if (!this.HasSample)
        {
            this.HasSample = true;
            this.Min = latest;
            this.Smoothed = latest;
            this.Variance = latest / 2;
            return;
        }

        this.Min = Math.Min(this.Min, latest);
        ackDelay = Math.Min(ackDelay, maxAckDelay);

        long adjusted = latest >= this.Min + ackDelay ? latest - ackDelay : latest;
        this.Variance = ((3 * this.Variance) + Math.Abs(this.Smoothed - adjusted)) / 4;
        this.Smoothed = ((7 * this.Smoothed) + adjusted) / 8;
    }

    /// <summary>Probe timeout without backoff: <c>srtt + max(4·rttvar, granularity) + max_ack_delay</c>.</summary>
    public long ProbeTimeout(long maxAckDelay) => this.Smoothed + Math.Max(4 * this.Variance, Granularity) + maxAckDelay;
}
