// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Middleware;
using Nalix.Runtime.Routing;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Audit;

/// <summary>
/// Reproductions for middleware pipeline audit findings (RT-01, RT-02).
/// Each test is expected to FAIL on the current code and documents the intended contract.
/// </summary>
[Trait("Category", "Audit")]
public sealed class PipelineAuditTests
{
    private static PacketContext<IPacket> CreateContext()
    {
        ObjectPoolManager pool = InstanceManager.Instance.GetOrCreateInstance<ObjectPoolManager>();
        return pool.Get<PacketContext<IPacket>>();
    }

    private static PacketHandler<IPacket> CreateThrowingHandler() =>
        new(0x0001, default, null, "ThrowingHandler", typeof(void),
            (_, _) => throw new InvalidOperationException("handler-boom"), null);

    /// <summary>
    /// Outbound middleware marked <c>AlwaysExecute</c>, counting invocations.
    /// </summary>
    [MiddlewareStage(MiddlewareStage.Outbound, AlwaysExecute = true)]
    private sealed class AlwaysOutboundMiddleware : IPacketMiddleware<IPacket>
    {
        public int Invocations;

        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next)
        {
            _ = Interlocked.Increment(ref this.Invocations);
            return next(context.CancellationToken);
        }
    }

    /// <summary>
    /// Plain outbound middleware (not AlwaysExecute), used to force a non-empty pipeline with no inbound stage.
    /// </summary>
    [MiddlewareStage(MiddlewareStage.Outbound)]
    private sealed class PlainOutboundMiddleware : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next) =>
            next(context.CancellationToken);
    }

    /// <summary>
    /// RT-01: <c>MiddlewareStage.AlwaysExecute</c> outbound middleware must run after the handler even when it
    /// throws (documented contract in .claude/skills/nalix-runtime.md, "Middleware Execution").
    /// The current pipeline propagates the handler exception before reaching the OutboundAlways stage, so the
    /// middleware is skipped and cleanup/audit work registered as AlwaysExecute is lost.
    /// </summary>
    [Fact]
    public void RT01_AlwaysExecuteOutboundMiddleware_RunsWhenHandlerThrows()
    {
        MiddlewarePipeline<IPacket> pipeline = new();
        PacketDispatchOptions<IPacket> dispatch = new();
        AlwaysOutboundMiddleware middleware = new();
        pipeline.Use(middleware);

        PacketContext<IPacket> ctx = CreateContext();

        Action act = () => pipeline.ExecuteAsync(ctx, dispatch, CreateThrowingHandler(), CancellationToken.None).GetAwaiter().GetResult();

        act.Should().Throw<InvalidOperationException>().WithMessage("*handler-boom*");
        middleware.Invocations.Should().Be(
            1,
            "AlwaysExecute middleware must run after the handler even when it throws");
    }

    private static ValueTask<object?> ThrowAfterYieldAsync() => Yield();

    private static async ValueTask<object?> Yield()
    {
        await Task.Yield();
        throw new InvalidOperationException("handler-boom-async");
    }

    /// <summary>
    /// Guard (not a repro, expected to PASS): an async handler that throws after yielding is converted to a FAIL
    /// control reply by <c>AwaitHandlerAndRespondAsync</c>, so the exception never reaches the pipeline and the
    /// OutboundAlways stage still runs. This bounds RT-01 to synchronous handler throws.
    /// </summary>
    [Fact]
    public async Task RT01_Guard_AlwaysExecuteOutboundMiddleware_RunsWhenAsyncHandlerThrows()
    {
        MiddlewarePipeline<IPacket> pipeline = new();
        PacketDispatchOptions<IPacket> dispatch = new();
        AlwaysOutboundMiddleware middleware = new();
        pipeline.Use(middleware);

        // Async dispatch sends a FAIL control reply, which needs a connection on the context.
        PacketContext<IPacket> ctx = PacketContextFactory.Create<IPacket>(new Nalix.Codec.ProtocolFrames.SessionResume(), new FakeConnection());
        PacketHandler<IPacket> handler = new(0x0001, default, null, "AsyncThrowingHandler", typeof(void),
            (_, _) => ThrowAfterYieldAsync(), null);

        await pipeline.ExecuteAsync(ctx, dispatch, handler, CancellationToken.None);

        middleware.Invocations.Should().Be(
            1,
            "OutboundAlways middleware runs after an async handler failure (converted to a FAIL reply)");
    }

    /// <summary>
    /// RT-02: when the terminal handler throws synchronously and the pipeline has no inbound middleware, the
    /// exception escapes <c>MiddlewarePipeline.ExecuteAsync</c> before <c>RECORD_EXECUTION</c> runs and before the
    /// runner is returned. <c>Metrics.ActiveExecutions</c> therefore drifts upward by one per such failure.
    /// </summary>
    [Fact]
    public void RT02_SyncHandlerException_DoesNotLeakActiveExecutionCounter()
    {
        MiddlewarePipeline<IPacket> pipeline = new();
        PacketDispatchOptions<IPacket> dispatch = new();
        pipeline.Use(new PlainOutboundMiddleware());

        PacketContext<IPacket> ctx = CreateContext();

        Action act = () => pipeline.ExecuteAsync(ctx, dispatch, CreateThrowingHandler(), CancellationToken.None).GetAwaiter().GetResult();
        act.Should().Throw<InvalidOperationException>();

        pipeline.Metrics.ActiveExecutions.Should().Be(
            0,
            "a failed execution must not remain counted as active");
    }
}
