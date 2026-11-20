// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Memory;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport;
using Xunit;

namespace Nalix.SDK.Tests.Transport;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class DatagramSessionDispatchTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);
    private static readonly MethodInfo s_dispatch = typeof(DatagramSession).GetMethod("DispatchDeliveries", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task DispatchDeliveries_FromTwoThreads_KeepsDeliveryOrder()
    {
        // The receive loop and the timer both dispatch; a second thread must not overtake a slow handler.
        using DatagramSession session = new(new FakeStreamSession());
        ConcurrentQueue<int> handled = new();
        using ManualResetEventSlim firstEntered = new();
        using ManualResetEventSlim releaseFirst = new();

        session.OnMessageReceived += (_, lease) =>
        {
            if (lease.Length == 20)
            {
                firstEntered.Set();
                _ = releaseFirst.Wait(s_timeout);
            }

            handled.Enqueue(lease.Length);
        };

        Deliver(session, 20);
        Task first = Task.Run(() => s_dispatch.Invoke(session, null));
        Assert.True(firstEntered.Wait(s_timeout));

        // Message 2 arrives while message 1's handler is still running.
        Deliver(session, 21);
        Task second = Task.Run(() => s_dispatch.Invoke(session, null));
        await Task.Delay(100);
        releaseFirst.Set();

        await Task.WhenAll(first, second).WaitAsync(s_timeout);

        Assert.Equal([20, 21], handled);
    }

    /// <summary>Queues a plain frame of <paramref name="length"/> bytes as if the engine had delivered it.</summary>
    private static void Deliver(DatagramSession session, int length)
    {
        Lock engineLock = (Lock)typeof(DatagramSession).GetField("_engineLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var deliveries = (List<(BufferLease Message, DeliveryMethod Method, byte Channel)>)typeof(DatagramSession)
            .GetField("_deliveries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;

        lock (engineLock)
        {
            deliveries.Add((BufferLease.CopyFrom(new byte[length]), DeliveryMethod.ReliableOrdered, 0));
        }
    }
}
