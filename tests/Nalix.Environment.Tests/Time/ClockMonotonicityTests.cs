// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Nalix.Environment.Tests.TestSupport;
using Nalix.Environment.Time;
using Xunit;

namespace Nalix.Environment.Tests.Time;

[Collection(ClockCollection.Name)]
public sealed class ClockMonotonicityTests
{
    private static void BusyWaitMs(double ms)
    {
        long ticks = (long)(ms / 1000.0 * Stopwatch.Frequency);
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < ticks)
        {
        }
    }

    [Fact]
    public void UnixTimeGetters_AfterBusyWait_IncreaseAndStayConsistent()
    {
        long s1 = Clock.UnixSecondsNow();
        long ms1 = Clock.UnixMillisecondsNow();
        long us1 = Clock.UnixMicrosecondsNow();
        long ticks1 = Clock.UnixTicksNow();

        BusyWaitMs(3);

        long s2 = Clock.UnixSecondsNow();
        long ms2 = Clock.UnixMillisecondsNow();
        long us2 = Clock.UnixMicrosecondsNow();
        long ticks2 = Clock.UnixTicksNow();

        Assert.True(s2 >= s1);
        Assert.True(ms2 > ms1);
        Assert.True(us2 > us1);
        Assert.True(ticks2 > ticks1);

        Assert.InRange(ms2 - ms1, 2, 1000);
        Assert.InRange(us2 - us1, 2000, 1_000_000);
    }
}
