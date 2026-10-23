// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Environment.Tests.TestSupport;

/// <summary>
/// Waits for the process tick clock (<see cref="System.Environment.TickCount64"/>) to advance, so tests that
/// depend on timeouts observe real elapsed time instead of trusting a blind sleep.
/// </summary>
public static class TickClock
{
    /// <summary>
    /// Blocks until <paramref name="milliseconds"/> have elapsed on <see cref="System.Environment.TickCount64"/>.
    /// </summary>
    public static void Advance(int milliseconds)
    {
        long target = System.Environment.TickCount64 + milliseconds;
        bool reached = SpinWait.SpinUntil(() => System.Environment.TickCount64 >= target, milliseconds + 10_000);

        Assert.True(reached, "The tick clock did not advance in time.");
    }
}
