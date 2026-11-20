// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Environment.Datagrams;
using Nalix.Network.Internal.Time;
using Xunit;

namespace Nalix.Network.Tests.Internal.Time;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class DatagramTimingWheelTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Schedule_FiresTheTargetAtItsDeadline()
    {
        DatagramTimingWheel wheel = new("test.dgram.timers");
        wheel.Activate(CancellationToken.None);
        try
        {
            Target target = new();
            long start = DatagramEngine.NowMicros();
            target.Arm(wheel, start + 20_000);

            await target.Fired.Task.WaitAsync(s_timeout);

            Assert.True(DatagramEngine.NowMicros() - start >= 19_000, "the deadline must not fire early");
            Assert.Equal(1, wheel.FiredCount);
        }
        finally
        {
            wheel.Deactivate();
        }
    }

    [Fact]
    public async Task Schedule_WhenRescheduledEarlier_FiresOnceAndDropsTheStaleEntry()
    {
        DatagramTimingWheel wheel = new("test.dgram.timers");
        wheel.Activate(CancellationToken.None);
        try
        {
            Target target = new();
            long now = DatagramEngine.NowMicros();
            target.Arm(wheel, now + 60_000);
            target.Arm(wheel, now + 10_000);

            await target.Fired.Task.WaitAsync(s_timeout);
            await Task.Delay(150);

            Assert.Equal(1, target.Count);
            Assert.True(wheel.StaleSkipCount >= 1);
        }
        finally
        {
            wheel.Deactivate();
        }
    }

    [Fact]
    public async Task Schedule_BeyondOneRevolution_WaitsForItsTick()
    {
        DatagramTimingWheel wheel = new("test.dgram.timers");
        wheel.Activate(CancellationToken.None);
        try
        {
            Target target = new();
            long start = DatagramEngine.NowMicros();
            target.Arm(wheel, start + 1_300_000);

            await target.Fired.Task.WaitAsync(s_timeout);

            Assert.True(DatagramEngine.NowMicros() - start >= 1_290_000, "a deadline more than one revolution away must not fire on the first pass");
            Assert.Equal(1, target.Count);
        }
        finally
        {
            wheel.Deactivate();
        }
    }

    private sealed class Target : DatagramTimingWheel.ITarget
    {
        private long _tick = long.MaxValue;
        private int _count;

        public TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => Volatile.Read(ref _count);

        public long ScheduledTick => Volatile.Read(ref _tick);

        public void Arm(DatagramTimingWheel wheel, long deadlineMicros)
        {
            long tick = wheel.ToTick(deadlineMicros);
            Volatile.Write(ref _tick, tick);
            wheel.Schedule(this, tick);
        }

        public void Execute()
        {
            Volatile.Write(ref _tick, long.MaxValue);
            _ = Interlocked.Increment(ref _count);
            _ = this.Fired.TrySetResult();
        }
    }
}
