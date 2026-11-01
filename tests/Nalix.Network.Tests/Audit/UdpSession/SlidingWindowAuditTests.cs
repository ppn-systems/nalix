// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Nalix.Network.Tests.Audit.UdpSession;

/// <summary>
/// Audit reproductions for the UDP anti-replay window (<c>SlidingWindow</c>).
/// Each test FAILS on the current code and documents a finding in <c>docs/audit/network-udp-session.md</c>.
/// </summary>
[Trait("Category", "Audit")]
public sealed class SlidingWindowAuditTests
{
    private static readonly Type s_slidingWindowType =
        Type.GetType("Nalix.Network.Internal.Security.SlidingWindow, Nalix.Network")
        ?? throw new InvalidOperationException("Unable to resolve Nalix.Network.Internal.Security.SlidingWindow.");

    private static readonly MethodInfo s_tryCheckMethod =
        s_slidingWindowType.GetMethod("TryCheck", [typeof(ushort)])
        ?? throw new InvalidOperationException("Unable to resolve SlidingWindow.TryCheck(ushort).");

    /// <summary>
    /// NETUDP-01: a sequence number that was already accepted and is now 60000 behind the newest
    /// sequence is accepted again. The 16-bit serial comparison treats any "behind" distance of
    /// 49152..65535 as a forward jump of 1..16384, so the replayed packet passes and rewinds the window.
    /// </summary>
    [Fact]
    public void TryCheck_ReplayOfSequenceFarBehindNewest_IsRejected()
    {
        object window = CreateWindow(windowSize: 1024);

        // Legitimate traffic advances in steps of at most 16384 (the accepted forward-jump bound).
        InvokeTryCheck(window, 0).Should().BeTrue();
        InvokeTryCheck(window, 16000).Should().BeTrue();
        InvokeTryCheck(window, 32000).Should().BeTrue();
        InvokeTryCheck(window, 48000).Should().BeTrue();
        InvokeTryCheck(window, 60000).Should().BeTrue();

        // Sequence 0 was accepted at the start and is a genuine replay now (60000 behind the newest).
        InvokeTryCheck(window, 0).Should().BeFalse(
            "sequence 0 was already accepted; a replay 60000 behind the newest must not be classified as a forward jump");
    }

    /// <summary>
    /// NETUDP-02: a legitimate forward jump larger than 16384 (for example a UDP loss burst of 20000
    /// datagrams) is rejected as "too old". The window never advances, so every following datagram is also
    /// rejected until the 16-bit counter wraps around (about 45000 datagrams later).
    /// </summary>
    [Fact]
    public void TryCheck_ForwardJumpAfterBurstLossBeyondQuarterRing_IsAccepted()
    {
        object window = CreateWindow(windowSize: 1024);

        InvokeTryCheck(window, 0).Should().BeTrue();

        // Client sent 20000 datagrams that were all lost, then sends the next one. This is a legitimate forward jump.
        InvokeTryCheck(window, 20000).Should().BeTrue(
            "a 20000-datagram burst loss is a legitimate forward jump; rejecting it desynchronizes the session");

        InvokeTryCheck(window, 20001).Should().BeTrue("subsequent legitimate datagrams must not be locked out");
    }

    private static object CreateWindow(int windowSize) =>
        Activator.CreateInstance(s_slidingWindowType, [windowSize])
        ?? throw new InvalidOperationException("Unable to instantiate SlidingWindow.");

    private static bool InvokeTryCheck(object window, ushort seq) =>
        (bool)(s_tryCheckMethod.Invoke(window, [seq])
               ?? throw new InvalidOperationException("SlidingWindow.TryCheck returned null."));
}
