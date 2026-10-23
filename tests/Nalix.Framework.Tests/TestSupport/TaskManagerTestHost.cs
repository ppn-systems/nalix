// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Xunit;

namespace Nalix.Framework.Tests.TestSupport;

internal sealed class TaskManagerTestHost : IDisposable
{
    private readonly List<TaskManager> _managers = [];

    public TaskManager CreateManager(TaskManagerOptions? options = null)
    {
        TaskManager manager = new(options ?? new TaskManagerOptions
        {
            CleanupInterval = TimeSpan.FromSeconds(5),
            DynamicAdjustmentEnabled = false,
            MaxWorkers = 8,
            IsEnableLatency = true
        });

        _managers.Add(manager);
        return manager;
    }

    public static TaskCompletionSource<T> CreateCompletionSource<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.True(condition(), "The expected condition was not satisfied before the timeout elapsed.");
    }

    public void Dispose()
    {
        foreach (TaskManager manager in _managers)
        {
            try
            {
                manager.Dispose();
            }
            catch
            {
            }
        }
    }
}
