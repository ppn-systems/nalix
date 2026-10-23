// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;
using Nalix.Environment.Memory;
using Xunit;

namespace Nalix.Environment.Tests.Memory;

/// <summary>
/// Stress tests to verify BufferLease thread safety and thread-hopping scenarios.
/// </summary>
public sealed class BufferLeaseStressTests
{
    [Fact]
    public async Task Rent_WhenThreadsHopAcrossAwaits_KeepsLeaseContentIntact()
    {
        // Run for 3 seconds to stress test the thread cache and shared pool under heavy hopping
        DateTime endTime = DateTime.UtcNow.AddSeconds(3);
        int tasksCount = 32;
        var tasks = new Task[tasksCount];

        for (int i = 0; i < tasksCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                while (DateTime.UtcNow < endTime)
                {
                    // Thread A rents a lease
                    BufferLease lease = BufferLease.Rent(64);

                    // Write a unique value to it
                    int uniqueVal = System.Environment.CurrentManagedThreadId ^ lease.GetHashCode();
                    lease.SpanFull[0] = (byte)(uniqueVal & 0xFF);

                    // Force thread hop using Task.Yield
                    await Task.Yield();

                    // Thread B continues, verify the value is still ours and hasn't been corrupted
                    Assert.Equal((byte)(uniqueVal & 0xFF), lease.SpanFull[0]);

                    // Dispose the lease
                    lease.Dispose();

                    // Thread B immediately rents another lease
                    BufferLease lease2 = BufferLease.Rent(64);
                    int uniqueVal2 = System.Environment.CurrentManagedThreadId ^ lease2.GetHashCode();
                    lease2.SpanFull[0] = (byte)(uniqueVal2 & 0xFF);

                    // Hop again
                    await Task.Yield();

                    // Verify and dispose
                    Assert.Equal((byte)(uniqueVal2 & 0xFF), lease2.SpanFull[0]);
                    lease2.Dispose();
                }
            });
        }

        await Task.WhenAll(tasks);
    }
}
