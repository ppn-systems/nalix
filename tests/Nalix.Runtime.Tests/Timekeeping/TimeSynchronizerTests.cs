// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Runtime.Timekeeping;
using Xunit;

namespace Nalix.Runtime.Tests.Timekeeping;

public sealed class TimeSynchronizerTests : IDisposable
{
    private readonly TimeSynchronizer _synchronizer;

    public TimeSynchronizerTests()
    {
        _synchronizer = new TimeSynchronizer();
    }

    [Fact]
    public void IsRunning_Initially_IsFalse()
    {
        Assert.False(_synchronizer.IsRunning);
    }

    [Fact]
    public void Activate_WhenCalled_StartsLoop()
    {
        _synchronizer.Activate();
        Assert.True(_synchronizer.IsRunning);
        Assert.True(_synchronizer.IsTimeSyncEnabled);
    }

    [Fact]
    public void Deactivate_AfterActivate_StopsLoop()
    {
        _synchronizer.Activate();
        _synchronizer.Deactivate();
        Assert.False(_synchronizer.IsRunning);
        Assert.False(_synchronizer.IsTimeSyncEnabled);
    }

    [Fact]
    public async Task Activate_WithShortPeriod_RaisesTimeSynchronizedPeriodically()
    {
        _synchronizer.Period = TimeSpan.FromMilliseconds(50);
        int tickCount = 0;
        TaskCompletionSource threeTicks = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _synchronizer.TimeSynchronized += tick =>
        {
            if (Interlocked.Increment(ref tickCount) >= 3)
            {
                _ = threeTicks.TrySetResult();
            }
        };

        _synchronizer.Activate();

        // Wait (bounded) for a few ticks
        _ = await Task.WhenAny(threeTicks.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        _synchronizer.Deactivate();

        Assert.True(tickCount >= 3, $"Expected at least 3 ticks, got {tickCount}");
    }

    [Fact]
    public void Period_WhenChangedWhileRunning_KeepsLoopRunningWithNewPeriod()
    {
        _synchronizer.Activate();
        _synchronizer.Period = TimeSpan.FromMilliseconds(100);
        
        Assert.True(_synchronizer.IsRunning);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _synchronizer.Period);
    }

    [Fact]
    public void Dispose_AfterActivate_StopsLoop()
    {
        _synchronizer.Activate();
        _synchronizer.Dispose();
        
        Assert.False(_synchronizer.IsRunning);
    }

    public void Dispose()
    {
        _synchronizer.Dispose();
    }
}
