// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;

namespace Nalix.Traversal.Tests.TestSupport;

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before the deadline.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
