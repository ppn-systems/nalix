// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Nalix.Codec.ProtocolFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport.Extensions;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Extensions;

/// <summary>
/// <see cref="RequestOptions.DeadlineMs"/> bounds the whole call across retries, and
/// <see cref="RequestOptions.RetryDelayMs"/> spaces the retries. The session never replies, so every attempt times out.
/// </summary>
[Collection(TestCollections.RealServer)]
public sealed class RequestDeadlineTests
{
    [Fact]
    public async Task RequestAsync_WithDeadline_StopsRetryingWhenTheBudgetIsSpent()
    {
        FakeSession session = new(isConnected: true);
        RequestOptions options = RequestOptions.Default.WithTimeout(1_000).WithRetry(5).WithDeadline(300);

        Stopwatch sw = Stopwatch.StartNew();
        _ = await Assert.ThrowsAsync<TimeoutException>(async () => await session.RequestAsync<Control>(new Control(), options));
        sw.Stop();

        // Without the deadline this call would take 6 x 1000 ms.
        Assert.InRange(sw.ElapsedMilliseconds, 250, 1_500);
        // One attempt uses the whole budget; a second may start only with the clock-skew sliver that is left.
        Assert.InRange(session.SendPacketCallCount, 1, 2);
    }

    [Fact]
    public async Task TryRequestAsync_WithDeadline_ReportsTimedOut()
    {
        FakeSession session = new(isConnected: true);
        RequestOptions options = RequestOptions.Default.WithTimeout(100).WithRetry(10).WithDeadline(250);

        Stopwatch sw = Stopwatch.StartNew();
        RequestOutcome<Control> outcome = await session.TryRequestAsync<Control>(new Control(), options);
        sw.Stop();

        Assert.Equal(RequestOutcomeKind.TimedOut, outcome.Kind);
        Assert.InRange(sw.ElapsedMilliseconds, 200, 1_500);
        Assert.InRange(session.SendPacketCallCount, 2, 4);
    }

    [Fact]
    public async Task RequestAsync_WithRetryDelay_WaitsBetweenAttempts()
    {
        FakeSession session = new(isConnected: true);
        RequestOptions options = RequestOptions.Default.WithTimeout(50).WithRetry(2).WithRetryDelay(100);

        Stopwatch sw = Stopwatch.StartNew();
        _ = await Assert.ThrowsAsync<TimeoutException>(async () => await session.RequestAsync<Control>(new Control(), options));
        sw.Stop();

        // 3 attempts x 50 ms + delays of ~100 ms and ~200 ms (each -20% at worst).
        Assert.True(sw.ElapsedMilliseconds >= 150 + 80 + 160, $"elapsed {sw.ElapsedMilliseconds} ms");
        Assert.Equal(3, session.SendPacketCallCount);
    }

    [Fact]
    public async Task RequestAsync_WhenRetryDelayWouldPassTheDeadline_DoesNotRetry()
    {
        FakeSession session = new(isConnected: true);
        RequestOptions options = RequestOptions.Default.WithTimeout(50).WithRetry(3).WithRetryDelay(1_000).WithDeadline(400);

        Stopwatch sw = Stopwatch.StartNew();
        _ = await Assert.ThrowsAsync<TimeoutException>(async () => await session.RequestAsync<Control>(new Control(), options));
        sw.Stop();

        Assert.Equal(1, session.SendPacketCallCount);
        Assert.True(sw.ElapsedMilliseconds < 400, $"elapsed {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RequestAsync_WithoutDeadlineOrDelay_KeepsPreviousRetryBehaviour()
    {
        FakeSession session = new(isConnected: true);
        RequestOptions options = RequestOptions.Default.WithTimeout(80).WithRetry(1);

        Stopwatch sw = Stopwatch.StartNew();
        _ = await Assert.ThrowsAsync<TimeoutException>(async () => await session.RequestAsync<Control>(new Control(), options));
        sw.Stop();

        Assert.Equal(2, session.SendPacketCallCount);
        Assert.InRange(sw.ElapsedMilliseconds, 140, 1_500);
    }

    [Fact]
    public void Validate_WithNegativeDeadlineOrDelay_Throws()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(RequestOptions.Default.WithDeadline(-1).Validate);
        _ = Assert.Throws<ArgumentOutOfRangeException>(RequestOptions.Default.WithRetryDelay(-1).Validate);
    }
}
