// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
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
using Xunit;

namespace Nalix.Runtime.Tests.Middleware;

public sealed class MiddlewarePipelineTests
{
    private static MiddlewarePipeline<IPacket> CreatePipeline() => new();
    private static PacketDispatchOptions<IPacket> CreateDispatch() => new();
    private static PacketContext<IPacket> CreateContext()
    {
        ObjectPoolManager pool = InstanceManager.Instance.GetOrCreateInstance<ObjectPoolManager>();
        return pool.Get<PacketContext<IPacket>>();
    }
    private static PacketHandler<IPacket> CreateHandler(Func<PacketContext<IPacket>, ValueTask<object?>> invoker)
    {
        return new PacketHandler<IPacket>(0x0001, default, null, "TestHandler", typeof(void),
            (obj, ctx) => invoker(ctx), null);
    }
    private static PacketHandler<IPacket> CreateNoopHandler() => CreateHandler(_ => default);

    [Fact]
    public void ExecuteAsync_WithMiddlewareRegisteredOutOfOrder_RunsInDeclaredOrder()
    {
        List<string> order = [];
        var pipeline = CreatePipeline();
        var dispatch = CreateDispatch();
        pipeline.Use(new OrderC_Middleware(order));
        pipeline.Use(new OrderA_Middleware(order));
        pipeline.Use(new OrderB_Middleware(order));
        var ctx = CreateContext();
        pipeline.ExecuteAsync(ctx, dispatch, CreateNoopHandler(), CancellationToken.None).GetAwaiter().GetResult();
        order.Should().Equal("A", "B", "C");
    }

    [Fact]
    public void ExecuteAsync_WhenMiddlewareShortCircuits_SkipsRemainingChainAndHandler()
    {
        List<string> order = [];
        var pipeline = CreatePipeline();
        var dispatch = CreateDispatch();
        // Order: A(100) -> ShortCircuit(200) -> C(300). ShortCircuit stops C and handler.
        pipeline.Use(new OrderA_Middleware(order));
        pipeline.Use(new ShortCircuitMiddleware());
        pipeline.Use(new OrderC_Middleware(order));
        var ctx = CreateContext();
        bool handlerCalled = false;
        var handler = CreateHandler(_ => { handlerCalled = true; return default; });
        pipeline.ExecuteAsync(ctx, dispatch, handler, CancellationToken.None).GetAwaiter().GetResult();
        order.Should().Equal("A");
        handlerCalled.Should().BeFalse();
    }

    [Fact]
    public void ExecuteAsync_WhenMiddlewareThrows_PropagatesException()
    {
        var pipeline = CreatePipeline();
        var dispatch = CreateDispatch();
        pipeline.Use(new ThrowingMiddleware());
        var ctx = CreateContext();
        Action act = () => pipeline.ExecuteAsync(ctx, dispatch, CreateNoopHandler(), CancellationToken.None).GetAwaiter().GetResult();
        act.Should().Throw<InvalidOperationException>().WithMessage("*test-exception*");
    }

    [Fact]
    public void ExecuteAsync_WithCancellationToken_InvokesHandler()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = CreatePipeline();
        var dispatch = CreateDispatch();
        bool handlerExecuted = false;
        var handler = CreateHandler(_ => { handlerExecuted = true; return default; });
        var ctx = CreateContext();
        pipeline.ExecuteAsync(ctx, dispatch, handler, cts.Token).GetAwaiter().GetResult();
        handlerExecuted.Should().BeTrue("the pipeline should execute the handler");
    }

    [Fact]
    public async Task ExecuteAsync_WithYieldingAsyncMiddleware_CompletesChainAndHandler()
    {
        List<string> order = [];
        var pipeline = CreatePipeline();
        var dispatch = CreateDispatch();
        pipeline.Use(new AsyncYieldingMiddleware(order));
        pipeline.Use(new OrderB_Middleware(order));
        var ctx = CreateContext();
        bool handlerCalled = false;
        var handler = CreateHandler(_ => { handlerCalled = true; return default; });
        await pipeline.ExecuteAsync(ctx, dispatch, handler, CancellationToken.None);
        order.Should().Contain("Async1");
        handlerCalled.Should().BeTrue();
    }

    [Fact]
    public void ExecuteAsync_WithNoMiddleware_InvokesHandlerDirectly()
    {
        var pipeline = CreatePipeline();
        var dispatch = CreateDispatch();
        bool handlerCalled = false;
        var handler = CreateHandler(_ => { handlerCalled = true; return default; });
        var ctx = CreateContext();
        pipeline.ExecuteAsync(ctx, dispatch, handler, CancellationToken.None).GetAwaiter().GetResult();
        handlerCalled.Should().BeTrue();
    }

    [Fact]
    public void ExecuteAsync_WithMultipleMiddleware_RunsAllThenHandlerInOrder()
    {
        List<string> order = [];
        var pipeline = CreatePipeline();
        var dispatch = CreateDispatch();
        pipeline.Use(new OrderA_Middleware(order));
        pipeline.Use(new OrderB_Middleware(order));
        pipeline.Use(new OrderC_Middleware(order));
        pipeline.Use(new OrderD_Middleware(order));
        bool handlerCalled = false;
        var handler = CreateHandler(_ => { handlerCalled = true; order.Add("Handler"); return default; });
        var ctx = CreateContext();
        pipeline.ExecuteAsync(ctx, dispatch, handler, CancellationToken.None).GetAwaiter().GetResult();
        order.Should().Equal("A", "B", "C", "D", "Handler");
        handlerCalled.Should().BeTrue();
    }

    [Fact]
    public void ExecuteAsync_WhenRepeated_DoesNotAllocatePerPacketDelegate()
    {
        // This test verifies the primary optimization: the pipeline no longer creates
        // a Func<CancellationToken, ValueTask> delegate + closure per packet.
        //
        // We compare pipeline allocation vs baseline (context get/dispose only).
        // The delta should be near zero, proving the pipeline itself is allocation-free.
        var pipeline = CreatePipeline();
        var dispatch = CreateDispatch();
        pipeline.Use(new NoOpMiddleware());
        var handler = CreateNoopHandler();

        // Warmup both paths
        for (int i = 0; i < 200; i++)
        {
            var ctx = CreateContext();
            pipeline.ExecuteAsync(ctx, dispatch, handler, CancellationToken.None).GetAwaiter().GetResult();
            ctx.Dispose();
        }

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        // Baseline: measure context get/dispose without pipeline
        long baselineBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            var ctx = CreateContext();
            ctx.Dispose();
        }
        long baselineAfter = GC.GetAllocatedBytesForCurrentThread();
        long baselineTotal = baselineAfter - baselineBefore;

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        // Pipeline: measure context get + pipeline execute + dispose
        long pipelineBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            var ctx = CreateContext();
            pipeline.ExecuteAsync(ctx, dispatch, handler, CancellationToken.None).GetAwaiter().GetResult();
            ctx.Dispose();
        }
        long pipelineAfter = GC.GetAllocatedBytesForCurrentThread();
        long pipelineTotal = pipelineAfter - pipelineBefore;

        // The delta (pipeline - baseline) should be near zero.
        // Old code allocated ~72 bytes/packet for delegate+closure = ~720,000 bytes for 10k packets.
        // New code should allocate near-zero extra for the pipeline path itself.
        long delta = pipelineTotal - baselineTotal;
        double deltaPerPacket = delta / 10_000.0;

        deltaPerPacket.Should().BeLessThan(20,
            $"pipeline should not add per-packet allocations beyond baseline (delta={delta}, per-packet={deltaPerPacket:F1})");
    }

    // --- Test middleware types ---

    [MiddlewareOrder(100)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    private sealed class OrderA_Middleware(List<string> log) : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next)
        {
            log.Add("A");
            return next(context.CancellationToken);
        }
    }

    [MiddlewareOrder(200)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    private sealed class OrderB_Middleware(List<string> log) : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next)
        {
            log.Add("B");
            return next(context.CancellationToken);
        }
    }

    [MiddlewareOrder(300)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    private sealed class OrderC_Middleware(List<string> log) : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next)
        {
            log.Add("C");
            return next(context.CancellationToken);
        }
    }

    [MiddlewareOrder(400)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    private sealed class OrderD_Middleware(List<string> log) : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next)
        {
            log.Add("D");
            return next(context.CancellationToken);
        }
    }

    [MiddlewareOrder(200)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    private sealed class ShortCircuitMiddleware : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next) => default;
    }

    [MiddlewareOrder(600)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    private sealed class ThrowingMiddleware : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next)
            => throw new InvalidOperationException("test-exception");
    }

    [MiddlewareOrder(150)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    private sealed class AsyncYieldingMiddleware(List<string> log) : IPacketMiddleware<IPacket>
    {
        public async ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next)
        {
            await Task.Yield();
            log.Add("Async1");
            await next(context.CancellationToken).ConfigureAwait(false);
        }
    }

    [MiddlewareOrder(0)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    private sealed class NoOpMiddleware : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next)
            => next(context.CancellationToken);
    }
}

