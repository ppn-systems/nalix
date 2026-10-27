// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Middleware.Standard;
using Nalix.Runtime.Routing;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Dispatching.Options;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PacketDispatchOptionsTimeoutLifetimeTests
{
    /// <summary>
    /// After a timeout the pipeline returns early, but the handler body is still running and still holds its
    /// context. The pooled context must not be recycled for another connection until the handler finishes.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WhenHandlerOutlivesTimeout_ContextIsNotRecycledToAnotherConnection()
    {
        PacketDispatchOptions<IPacket> options = new PacketDispatchOptions<IPacket>()
            .WithMiddleware(new TimeoutMiddleware());

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<object?> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        PacketHandler<IPacket> slow = PacketHandlerFactory.CreateDescriptor<IPacket>(
            async (_, c) =>
            {
                await gate.Task;
                try
                {
                    observed.TrySetResult(c.Connection);
                }
                catch (Exception ex)
                {
                    observed.TrySetResult(ex);
                }

                return null;
            },
            timeoutMs: 50);

        PacketHandler<IPacket> cheap = PacketHandlerFactory.CreateDescriptor<IPacket>(
            (_, _) => new ValueTask<object?>((object?)null));

        FakeConnection connA = new();
        FakeConnection connB = new();
        try
        {
            Task dispatchA = options.ExecuteResolvedHandlerAsync(
                slow, new TestPacket(), connA, reliable: true, encryptedOnWire: false).AsTask();

            // Wait until the timeout fired and the TIMEOUT directive went out.
            for (int i = 0; i < 200 && connA.FakeTcp.SentMessages.Count == 0; i++)
            {
                await Task.Delay(10);
            }

            connA.FakeTcp.SentMessages.Should().NotBeEmpty();

            await Task.Delay(100); // let the pipeline unwind after the timeout

            // Another connection dispatches while A's handler is still running.
            await options.ExecuteResolvedHandlerAsync(
                cheap, new TestPacket(), connB, reliable: true, encryptedOnWire: false).AsTask();

            gate.SetResult();
            object? seen = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatchA.WaitAsync(TimeSpan.FromSeconds(5));

            seen.Should().BeSameAs(connA, "a handler that outlived its timeout must still see its own connection");
        }
        finally
        {
            gate.TrySetResult();
            connA.Dispose();
            connB.Dispose();
        }
    }
}
