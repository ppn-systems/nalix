// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport.Internal;

namespace Nalix.SDK.Tests.Transport.Internal;

[Collection(TestCollections.RealServer)]
public sealed class PendingRequestTests
{
    [Fact]
    public async Task OnMessageReceived_WhenRequestIsReturnedAndRerentedDuringPredicate_DoesNotCompleteNewRequest()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();

        using BufferLease lease = BufferLease.CopyFrom(new Control().Serialize());
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        using CancellationTokenSource cts = new();

        PendingRequest<Control> a = PendingRequest<Control>.Rent(session, _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return true;
        });
        a.Subscribe();
        ValueTask<Control> aTask = a.WaitAsync(0, cts.Token);

        // The session has snapshotted A and is now inside A's predicate, outside any lock.
        Task deliver = Task.Run(() => a.OnMessageReceived(lease));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        // A is canceled and returned to the pool, then immediately re-rented as B (same pooled instance).
        await cts.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await aTask);
        a.Return(wasAwaited: true);

        PendingRequest<Control> b = PendingRequest<Control>.Rent(session, _ => false);
        b.Subscribe();
        ValueTask<Control> bTask = b.WaitAsync(0, CancellationToken.None);

        release.Set();
        await deliver;

        bool completedWithStaleFrame = bTask.IsCompleted;
        if (completedWithStaleFrame)
        {
            (await bTask).Dispose();
        }

        b.Return(wasAwaited: completedWithStaleFrame);

        Assert.False(completedWithStaleFrame, "A's frame was delivered to the request that re-rented the pooled instance.");
        Assert.Same(a, b);
    }

    [Fact]
    public async Task OnTimeout_WhenTimerFiresAfterReturnAndRerent_DoesNotTimeOutNewRequest()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();

        using BufferLease lease = BufferLease.CopyFrom(new Control().Serialize());

        int bogus = 0;
        for (int i = 0; i < 10_000; i++)
        {
            PendingRequest<Control> a = PendingRequest<Control>.Rent(session, _ => true);
            a.Subscribe();
            ValueTask<Control> aTask = a.WaitAsync(1, CancellationToken.None);

            // Reply right around the 1 ms timer boundary so the timer callback is queued while the reply completes A.
            long target = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * (700 + ((i % 8) * 100)) / 1_000_000);
            while (Stopwatch.GetTimestamp() < target)
            {
                Thread.SpinWait(20);
            }

            a.OnMessageReceived(lease);

            try
            {
                (await aTask).Dispose();
            }
            catch (TimeoutException)
            {
            }

            a.Return(wasAwaited: true);

            PendingRequest<Control> b = PendingRequest<Control>.Rent(session, _ => false);
            b.Subscribe();
            ValueTask<Control> bTask = b.WaitAsync(0, CancellationToken.None); // no timeout: must never complete by itself

            long settle = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 5000);
            while (Stopwatch.GetTimestamp() < settle)
            {
                Thread.SpinWait(20);
            }

            bool bogusTimeout = bTask.IsCompleted;
            if (bogusTimeout)
            {
                bogus++;
            }

            b.Return(wasAwaited: bogusTimeout);
        }

        Assert.Equal(0, bogus);
    }
}
