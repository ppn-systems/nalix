// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Environment.Tests.TestSupport;
using Nalix.Environment.Time;
using Xunit;

namespace Nalix.Environment.Tests.Time;

[Collection(ClockCollection.Name)]
public sealed class ClockConcurrencyTests
{
    [Fact]
    public async Task NowUtc_WhenCalledFromManyThreads_DoesNotThrow()
    {
        const int ThreadCount = 10;
        const int IterationsPerThread = 1000;
        List<Task> tasks = [];

        for (int i = 0; i < ThreadCount; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    _ = Clock.NowUtc();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task NowUtcAndUnixMillisecondsNow_WhenReadConcurrently_ThrowNoExceptions()
    {
        const int ThreadCount = 4;
        const int IterationsPerThread = 100;
        List<Task> tasks = [];
        List<Exception> exceptions = [];
        object lockObj = new();

        for (int i = 0; i < ThreadCount; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int j = 0; j < IterationsPerThread; j++)
                    {
                        _ = Clock.NowUtc();
                        _ = Clock.UnixMillisecondsNow();
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task NowUtc_WhenReadRepeatedlyPerThread_ReturnsMonotonicValues()
    {
        const int ThreadCount = 5;
        const int IterationsPerThread = 100;
        List<Task<bool>> tasks = [];

        for (int i = 0; i < ThreadCount; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                DateTime prev = Clock.NowUtc();
                for (int j = 0; j < IterationsPerThread; j++)
                {
                    DateTime current = Clock.NowUtc();
                    if (current < prev)
                    {
                        return false;
                    }
                    prev = current;
                    Thread.Sleep(1);
                }
                return true;
            }));
        }

        _ = await Task.WhenAll(tasks);

        Assert.All(tasks, task => Assert.True(task.Result));
    }
}
