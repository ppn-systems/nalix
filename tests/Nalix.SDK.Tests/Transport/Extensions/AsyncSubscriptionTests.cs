// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
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

        using DiagnosticCapture capture = new(DiagnosticsEvents.Subscriptions.HandlerFailed);
        IEnumerable<DiagnosticLog> errors = capture.Events.Select(e => e.Log);
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
        Assert.Equal("SDK.TransportSessionSubscriptions:OnAsync", error.Tag);
        Assert.Contains("api=On<Control>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_WithAsyncHandlerThatThrowsBeforeAwait_LogsInsteadOfCrashing()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());

        using DiagnosticCapture capture = new(DiagnosticsEvents.Subscriptions.HandlerFailed);
        IEnumerable<DiagnosticLog> errors = capture.Events.Select(e => e.Log);

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

        using DiagnosticCapture capture = new(DiagnosticsEvents.Subscriptions.HandlerFailed);
        IEnumerable<DiagnosticLog> errors = capture.Events.Select(e => e.Log);

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

    [Fact]
    public void On_WithSyncHandlerThatThrows_ReportsHandlerFailed()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());
        using DiagnosticCapture capture = new(DiagnosticsEvents.Subscriptions.HandlerFailed);

        using (session.On<Control>((Action<Control>)(_ => throw new InvalidOperationException("sync handler boom"))))
        {
            session.RaiseMessage();
        }

        Assert.Contains(capture.Events, e => e.Log.Exception?.Message == "sync handler boom" && e.Log.Tag == "SDK.TransportSessionSubscriptions:On");
    }

    [Fact]
    public void OnExact_WithOtherPacketTypeOrThrowingHandler_ReportsBoth()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());
        using DiagnosticCapture capture = new("Subscriptions.");

        using (session.OnExact<Directive>(_ => { }))
        using (session.OnExact<Control>(_ => throw new InvalidOperationException("exact boom")))
        {
            session.RaiseMessage();
        }

        Assert.Contains(capture.Events, e => e.Name == DiagnosticsEvents.Subscriptions.UnexpectedPacket && e.Log.Message.Contains("expected=Directive", StringComparison.Ordinal));
        Assert.Contains(capture.Events, e => e.Name == DiagnosticsEvents.Subscriptions.HandlerFailed && e.Log.Exception?.Message == "exact boom");
    }

    [Fact]
    public void On_WithTaskHandlerThatThrowsSynchronously_ReportsHandlerFailed()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());
        using DiagnosticCapture capture = new(DiagnosticsEvents.Subscriptions.HandlerFailed);

        // A non-async lambda returning Task that throws before producing one.
        using (session.On<Control>((Func<Control, Task>)(_ => throw new InvalidOperationException("no task"))))
        {
            session.RaiseMessage();
        }

        Assert.Contains(capture.Events, e => e.Log.Exception?.Message == "no task");
    }

    [Fact]
    public void On_WithTaskHandlerThatCompletesSynchronously_ReportsNothing()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());
        using DiagnosticCapture capture = new("Subscriptions.");
        int calls = 0;

        using (session.On<Control>(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }))
        {
            session.RaiseMessage();
        }

        Assert.Equal(1, calls);
        Assert.Empty(capture.Events);
    }

    [Fact]
    public async Task On_WithAsyncHandlerThatIsCanceled_ReportsHandlerCanceled()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());
        using DiagnosticCapture capture = new(DiagnosticsEvents.Subscriptions.HandlerCanceled);

        using (session.On<Control>(async _ =>
        {
            await Task.Yield();
            throw new OperationCanceledException("canceled handler");
        }))
        {
            session.RaiseMessage();
            await WaitUntilAsync(() => capture.Events.Any(e => e.Log.Exception?.Message == "canceled handler"));
        }
    }

    [Fact]
    public void OnOnce_WithThrowingHandler_ReportsHandlerFailed()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.SetNextPacket(new Control());
        using DiagnosticCapture capture = new(DiagnosticsEvents.Subscriptions.HandlerFailed);

#pragma warning disable CS0618 // OnOnce is obsolete but still shipped.
        using (session.OnOnce<Control>(_ => true, _ => throw new InvalidOperationException("once boom")))
#pragma warning restore CS0618
        {
            session.RaiseMessage();
        }

        Assert.Contains(capture.Events, e => e.Log.Exception?.Message == "once boom" && e.Log.Tag == "SDK.TransportSessionSubscriptions:OnOnce");
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
