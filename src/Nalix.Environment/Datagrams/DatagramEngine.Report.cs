// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Globalization;
using System.Net;

namespace Nalix.Environment.Datagrams;

public sealed partial class DatagramEngine
{
    /// <summary>Gets the current path MTU (UDP payload bytes).</summary>
    public int Mtu => _mtuDiscovery.Mtu;

    /// <summary>Gets the smoothed round-trip time in microseconds.</summary>
    public long SmoothedRtt => _rtt.Smoothed;

    /// <summary>Gets the congestion window in bytes.</summary>
    public long CongestionWindow => _congestion.Window;

    /// <summary>Gets the bytes in flight.</summary>
    public long BytesInFlight => _congestion.BytesInFlight;

    /// <summary>Gets the number of datagrams sent.</summary>
    public long DatagramsSent { get; private set; }

    /// <summary>Gets the number of authenticated datagrams received.</summary>
    public long DatagramsReceived { get; private set; }

    /// <summary>Gets the number of received datagrams that were dropped (malformed, duplicate, or not authentic).</summary>
    public long DatagramsDropped { get; private set; }

    /// <summary>Gets the number of sent datagrams declared lost.</summary>
    public long DatagramsLost { get; private set; }

    /// <summary>Gets the bytes the receive side currently holds back for ordering and reassembly.</summary>
    internal long ReceiverHeldBytes => _receiver.HeldBytes;

    /// <summary>
    /// Gets the validated address of the peer on the server, or <see langword="null"/> before the first
    /// authenticated datagram and on the client.
    /// </summary>
    public SocketAddress? ActivePath => _path.Active;

    /// <summary>
    /// Gets the time of the next <see cref="OnTimer"/> call, or <see cref="long.MaxValue"/> when none is needed.
    /// </summary>
    public long NextTimer
    {
        get
        {
            if (_state == DatagramEngineState.Closed)
            {
                return long.MaxValue;
            }

            if (_state == DatagramEngineState.Draining)
            {
                return _drainEnd;
            }

            long next = Math.Min(_ackTracker.AckDeadline, this.LossOrProbeTime());
            next = Math.Min(next, _lastReceiveTime + this.IdleTimeout());
            next = Math.Min(next, _discardPreviousKeyAt);
            next = Math.Min(next, _nextFragmentSweep);

            if (!_pingPending)
            {
                next = Math.Min(next, _lastAckElicitingSent + (_idleTimeout / 3));
            }

            next = Math.Min(next, _path.NextChallengeTime);
            next = Math.Min(next, _mtuDiscovery.NextProbeTime);

            if (_sender.MayHaveSendable)
            {
                next = Math.Min(next, _pacingBlockedUntil);
            }

            return next;
        }
    }

    /// <summary>
    /// Gets a one-line summary of the session statistics, for diagnostics.
    /// </summary>
    public string GenerateReport()
        => string.Create(CultureInfo.InvariantCulture,
            $"state={_state} mtu={_mtuDiscovery.Mtu} srtt={_rtt.Smoothed}us cwnd={_congestion.Window} inflight={_congestion.BytesInFlight} " +
            $"sent={this.DatagramsSent} received={this.DatagramsReceived} lost={this.DatagramsLost} dropped={this.DatagramsDropped}");
}
