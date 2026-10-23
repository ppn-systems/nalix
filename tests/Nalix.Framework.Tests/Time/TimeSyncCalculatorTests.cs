// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.SDK;
using Xunit;

namespace Nalix.Framework.Tests.Time;

[Collection(TestCollections.Clock)]
public sealed class TimeSyncCalculatorTests
{
    [Fact]
    public void CalculateOffsetMs_WhenServerTimeIsNegative_ThrowsArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TimeSyncCalculator.CalculateOffsetMs(-1000, 0, 1000, 10000));

        Assert.Contains("Server Unix timestamp cannot be negative", ex.Message);
        Assert.Equal("serverUnixMs", ex.ParamName);
    }

    [Fact]
    public void CalculateOffsetMs_WhenRttIsNegative_ThrowsArgumentException()
    {
        long currentUnixMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TimeSyncCalculator.CalculateOffsetMs(currentUnixMs, -10, 1000, 10000));

        Assert.Contains("RTT cannot be negative", ex.Message);
        Assert.Equal("rttMs", ex.ParamName);
    }

    [Fact]
    public void CalculateOffsetMs_WhenMaxAllowedDriftIsZero_ThrowsArgumentException()
    {
        long currentUnixMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TimeSyncCalculator.CalculateOffsetMs(currentUnixMs, 0, 0, 10000));

        Assert.Contains("Max allowed drift must be positive", ex.Message);
        Assert.Equal("maxAllowedDriftMs", ex.ParamName);
    }

    [Fact]
    public void CalculateOffsetMs_WhenMaxAllowedDriftIsNegative_ThrowsArgumentException()
    {
        long currentUnixMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TimeSyncCalculator.CalculateOffsetMs(currentUnixMs, 0, -1000, 10000));

        Assert.Contains("Max allowed drift must be positive", ex.Message);
        Assert.Equal("maxAllowedDriftMs", ex.ParamName);
    }

    [Fact]
    public void CalculateOffsetMs_WhenMaxHardAdjustIsZero_ThrowsArgumentException()
    {
        long currentUnixMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TimeSyncCalculator.CalculateOffsetMs(currentUnixMs, 0, 1000, 0));

        Assert.Contains("Max hard adjust must be positive", ex.Message);
        Assert.Equal("maxHardAdjustMs", ex.ParamName);
    }

    [Fact]
    public void CalculateOffsetMs_WhenMaxHardAdjustIsNegative_ThrowsArgumentException()
    {
        long currentUnixMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TimeSyncCalculator.CalculateOffsetMs(currentUnixMs, 0, 1000, -10000));

        Assert.Contains("Max hard adjust must be positive", ex.Message);
        Assert.Equal("maxHardAdjustMs", ex.ParamName);
    }

    [Fact]
    public void CalculateOffsetMs_WhenTimestampIsBefore2000_ThrowsArgumentException()
    {
        const long oldTimestamp = 915148800000L;

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TimeSyncCalculator.CalculateOffsetMs(oldTimestamp, 0, 1000, 10000));

        Assert.Contains("Server Unix timestamp is outside reasonable range", ex.Message);
        Assert.Equal("serverUnixMs", ex.ParamName);
    }

    [Fact]
    public void CalculateOffsetMs_WhenTimestampIsAfter2100_ThrowsArgumentException()
    {
        const long futureTimestamp = 4133980800000L;

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TimeSyncCalculator.CalculateOffsetMs(futureTimestamp, 0, 1000, 10000));

        Assert.Contains("Server Unix timestamp is outside reasonable range", ex.Message);
        Assert.Equal("serverUnixMs", ex.ParamName);
    }

    [Fact]
    public void CalculateOffsetMs_WhenTimestampIsNearNow_ReturnsExpectedOffset()
    {
        long futureUnixMs = (long)(DateTime.UtcNow.AddSeconds(10) - DateTime.UnixEpoch).TotalMilliseconds;
        double offset = TimeSyncCalculator.CalculateOffsetMs(futureUnixMs, 10, 50000, 50000);

        Assert.InRange(Math.Abs(offset), 9000, 11000);
    }

    [Fact]
    public void CalculateOffsetMs_WhenTimestampIsExactly2000_DoesNotThrow()
    {
        const long y2kTimestamp = 946684800000L;
        _ = TimeSyncCalculator.CalculateOffsetMs(y2kTimestamp, 0, 100000, 100000);
    }

    [Fact]
    public void CalculateOffsetMs_WhenTimestampIsExactly2100_DoesNotThrow()
    {
        const long y2100Timestamp = 4102444800000L;
        _ = TimeSyncCalculator.CalculateOffsetMs(y2100Timestamp, 0, 100000, 100000);
    }

    [Fact]
    public void CalculateOffsetMs_WhenRttProvided_AccountsForRoundTripTime()
    {
        long futureUnixMs = (long)(DateTime.UtcNow.AddSeconds(10) - DateTime.UnixEpoch).TotalMilliseconds;
        const double rttMs = 100;

        double offset = TimeSyncCalculator.CalculateOffsetMs(futureUnixMs, rttMs, 50000, 50000);

        Assert.InRange(Math.Abs(offset), 9000, 11000);
    }
}
