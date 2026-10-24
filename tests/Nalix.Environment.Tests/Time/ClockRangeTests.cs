// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Tests.TestSupport;
using Nalix.Environment.Time;
using Xunit;

namespace Nalix.Environment.Tests.Time;

[Collection(ClockCollection.Name)]
public sealed class ClockRangeTests
{
    [Fact]
    public void UnixSecondsNowUInt32_WhenCalled_ReturnsPositiveValueBelowMax()
    {
        uint result = Clock.UnixSecondsNowUInt32();

        Assert.True(result > 0);
        Assert.True(result < uint.MaxValue);
    }

    [Fact]
    public void UnixSecondsNowUInt32_WhenCalled_ReturnsValueBetween2020And2030()
    {
        uint result = Clock.UnixSecondsNowUInt32();

        const uint y2020 = 1577836800;
        const uint y2030 = 1893456000;

        Assert.InRange(result, y2020, y2030);
    }

    [Fact]
    public void UnixMillisecondsNow_WhenCalled_ReturnsValueBetween2020And2030()
    {
        long result = Clock.UnixMillisecondsNow();

        Assert.True(result > 0);

        const long y2020Ms = 1577836800000L;
        const long y2030Ms = 1893456000000L;

        Assert.InRange(result, y2020Ms, y2030Ms);
    }

    [Fact]
    public void UnixMicrosecondsNow_WhenCalled_ReturnsValueBetween2020And2030()
    {
        long result = Clock.UnixMicrosecondsNow();

        Assert.True(result > 0);

        const long y2020Us = 1577836800000000L;
        const long y2030Us = 1893456000000000L;

        Assert.InRange(result, y2020Us, y2030Us);
    }
}
