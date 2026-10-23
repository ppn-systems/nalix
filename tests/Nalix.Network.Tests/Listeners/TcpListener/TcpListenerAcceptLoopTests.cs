// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Network.Listeners.Tcp;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners.TcpListener;

/// <summary>
/// Regression tests for the 2026-07-28 prod incident: a transient ListenerClosed/SocketAborted
/// accept result (no real cancellation) used to permanently break AcceptConnectionsAsync, killing
/// the only accept-worker (MaxParallel defaulted to 1) while the process stayed alive.
/// </summary>
public sealed class TcpListenerAcceptLoopTests
{
    private sealed class StubTcpListener : TcpListenerBase
    {
        public StubTcpListener(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
            : base(12348, protocol, hub, guard)
        {
        }

        public Task RunAcceptLoop(IWorkerContext ctx, CancellationToken token) => AcceptConnectionsAsync(ctx, token);

        public void KillListenerSocket() =>
            typeof(TcpListenerBase).GetField("_listener", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(this, null);
    }

    private sealed class CountingWorkerContext(CancellationToken token) : IWorkerContext
    {
        public int BeatCount;

        public ISnowflake Id => null!;
        public string Name => "test";
        public string Group => "test";
        public bool IsCancellationRequested => token.IsCancellationRequested;

        public void Beat() => Interlocked.Increment(ref BeatCount);
        public void Advance(long delta, string? note = null) { }
    }

    [Fact]
    public async Task AcceptConnectionsAsync_WhenListenerSocketClosed_RetriesInsteadOfBreaking()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        using var listener = new StubTcpListener(protocol, hub, guard);

        // _listener == null -> CreateConnectionAsync returns ListenerClosed on every call,
        // with no cancellation ever requested until we say so below.
        listener.KillListenerSocket();

        using var cts = new CancellationTokenSource();
        var ctx = new CountingWorkerContext(cts.Token);

        Task loopTask = listener.RunAcceptLoop(ctx, cts.Token);

        // Old (buggy) behavior: loop breaks after the first ListenerClosed -> exactly 1 Beat().
        // Fixed behavior: loop retries every 50ms -> multiple Beats while uncancelled.
        await Task.Delay(220);
        loopTask.IsCompleted.Should().BeFalse("a transient ListenerClosed without cancellation must not break the accept loop");
        Volatile.Read(ref ctx.BeatCount).Should().BeGreaterThan(1, "the loop must retry (heartbeat again) instead of exiting after one transient failure");

        // Now request real shutdown -> loop must still terminate correctly (no regression).
        await cts.CancelAsync();
        Task completed = await Task.WhenAny(loopTask, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().Be(loopTask, "the loop must still exit promptly once cancellation is actually requested");
    }
}
