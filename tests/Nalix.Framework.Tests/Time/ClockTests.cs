// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
using System;
using Nalix.Environment.Time;
using Xunit;

namespace Nalix.Framework.Tests.Time;

[Collection(TestCollections.Clock)]
public sealed class ClockTests
{
    [Fact]
    public void UnixSecondsNowShouldMatch()
    {
        DateTime before = DateTime.UtcNow;
        long unix = Clock.UnixSecondsNow();
        DateTime after = DateTime.UtcNow;

        long minExpected = (long)(before - DateTime.UnixEpoch).TotalSeconds - 1;
        long maxExpected = (long)(after - DateTime.UnixEpoch).TotalSeconds + 1;

        Assert.InRange(unix, minExpected, maxExpected);
    }

    [Fact]
    public void UnixMillisecondsNowShouldMatchRawUtcWindow()
    {
        DateTime before = DateTime.UtcNow;
        long unix = Clock.UnixMillisecondsNow();
        DateTime after = DateTime.UtcNow;

        long minExpected = (long)(before - DateTime.UnixEpoch).TotalMilliseconds - 2500;
        long maxExpected = (long)(after - DateTime.UnixEpoch).TotalMilliseconds + 3000;

        Assert.InRange(unix, minExpected, maxExpected);
    }

    [Fact]
    public void UnixMicrosecondsNowShouldMatch()
    {
        DateTime before = DateTime.UtcNow;
        long unix = Clock.UnixMicrosecondsNow();
        DateTime after = DateTime.UtcNow;

        long minExpected = ((before - DateTime.UnixEpoch).Ticks / 10) - 50_000;
        long maxExpected = ((after - DateTime.UnixEpoch).Ticks / 10) + 50_000;

        Assert.InRange(unix, minExpected, maxExpected);
    }

    [Fact]
    public void UnixTicksNowShouldMatch()
    {
        DateTime before = DateTime.UtcNow;
        long unix = Clock.UnixTicksNow();
        DateTime after = DateTime.UtcNow;

        long minExpected = (before - DateTime.UnixEpoch).Ticks - 500_000;
        long maxExpected = (after - DateTime.UnixEpoch).Ticks + 500_000;

        Assert.InRange(unix, minExpected, maxExpected);
    }

    [Fact]
    public void NowUtcShouldIncreaseAcrossSequentialReads()
    {
        DateTime first = Clock.NowUtc();
        DateTime second = Clock.NowUtc();
        DateTime third = Clock.NowUtc();

        Assert.True(second >= first);
        Assert.True(third >= second);
    }
}















