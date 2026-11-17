// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.ProtocolFrames;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport.Extensions;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Extensions;

/// <summary>
/// An <c>async</c> lambda passed to <c>On</c> binds to the <see cref="Func{T, Task}"/> overload. Before it existed the
/// lambda became <c>async void</c>, and an exception after its first await terminated the process (in a test run, the
/// test host). These tests would crash the host on the old code rather than fail.
/// </summary>
[Collection(TestCollections.RealServer)]
public sealed class AsyncSubscriptionTests
{
    [Fact]
    public async Task On_WithAsyncHandlerThatThrowsAfterAwait_LogsInsteadOfCrashing()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());

        ConcurrentQueue<DiagnosticLog> errors = new();
        using IDisposable logs = DiagnosticsEvents.SubscribeLogs((_, log) => errors.Enqueue(log), DiagnosticsEvents.Internal.Error);
        TaskCompletionSource ran = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using (session.On<Control>(async _ =>
        {
            await Task.Yield();
            ran.SetResult();
            throw new InvalidOperationException("async handler boom");
        }))
        {
            session.RaiseMessage();
            await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => errors.Any(e => e.Exception?.Message == "async handler boom"));
        }

        DiagnosticLog error = errors.First(e => e.Exception?.Message == "async handler boom");
        Assert.Equal("SDK.Subscriptions", error.Tag);
        Assert.Contains("On<Control>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_WithAsyncHandlerThatThrowsBeforeAwait_LogsInsteadOfCrashing()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());

        ConcurrentQueue<DiagnosticLog> errors = new();
        using IDisposable logs = DiagnosticsEvents.SubscribeLogs((_, log) => errors.Enqueue(log), DiagnosticsEvents.Internal.Error);

#pragma warning disable CS1998 // The handler is async on purpose and throws before any await.
        using (session.On<Control>(async _ => throw new InvalidOperationException("sync part boom")))
#pragma warning restore CS1998
        {
            session.RaiseMessage();
        }

        await WaitUntilAsync(() => errors.Any(e => e.Exception?.Message == "sync part boom"));
    }

    [Fact]
    public async Task On_WithAsyncHandlerThatAwaits_ReleasesTheReceiveLoopForTheNextPacket()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;

        using (session.On<Control>(async packet =>
        {
            _ = Interlocked.Increment(ref started);
            await gate.Task;
        }))
        {
            // Both raises return while the first handler is still awaiting, so the receive loop is not blocked by it.
            session.RaiseMessage();
            session.RaiseMessage();
            Assert.Equal(2, Volatile.Read(ref started));
            gate.SetResult();
        }
    }

    [Fact]
    public async Task On_WithPredicateAndAsyncHandlerThatThrows_LogsInsteadOfCrashing()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());

        ConcurrentQueue<DiagnosticLog> errors = new();
        using IDisposable logs = DiagnosticsEvents.SubscribeLogs((_, log) => errors.Enqueue(log), DiagnosticsEvents.Internal.Error);

        using (session.On(static (IPacket p) => p is Control, async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("predicate handler boom");
        }))
        {
            session.RaiseMessage();
            await WaitUntilAsync(() => errors.Any(e => e.Exception?.Message == "predicate handler boom"));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "the expected log entry was not written");
    }
}
