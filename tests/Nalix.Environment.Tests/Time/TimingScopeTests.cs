// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading;
using Nalix.Environment.Time;
using Xunit;

namespace Nalix.Environment.Tests.Time;

public sealed class TimingScopeTests
{
    [Fact]
    public void Start_WhenElapsed_ReturnsPositiveTicksAndMilliseconds()
    {
        TimingScope scope = TimingScope.Start();
        Thread.Sleep(5);

        long ticks = scope.ElapsedTicks;
        double milliseconds = scope.GetElapsedMilliseconds();

        Assert.True(ticks > 0);
        Assert.True(milliseconds >= 0);
    }

    [Fact]
    public void GetElapsedMilliseconds_WhenReadRepeatedly_IsNonDecreasing()
    {
        TimingScope scope = TimingScope.Start();
        Thread.Sleep(2);
        double first = scope.GetElapsedMilliseconds();
        Thread.Sleep(2);
        double second = scope.GetElapsedMilliseconds();

        Assert.True(second >= first);
    }
}
