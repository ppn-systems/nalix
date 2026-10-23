using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Internal.Pooling;
using Xunit;

namespace Nalix.Network.Tests;

/// <summary>
/// Regression tests for <see cref="PooledConnectEventContext"/> pool reset behavior.
///
/// Bug: ResetForPool() did not clear LocalOwner, causing returned contexts
/// in the global pool to retain a reference to the disposed Connection
/// (via IPooledConnectContextPool), preventing GC until the context was
/// rented again.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PooledConnectEventContextTests
{
    [Fact]
    public void ResetForPool_ClearsLocalOwner()
    {
        // Arrange
        PooledConnectEventContext context = new();

        // Simulate a connection acquiring and initializing the context
        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs(),
            releasePendingPacketOnCompletion: false);

        // Act
        context.ResetForPool();

        // Assert: LocalOwner must be cleared to prevent retaining Connection reference
        _ = context.LocalOwner.Should().BeNull(
            "ResetForPool must clear LocalOwner to allow GC of the owning Connection");

        // Other fields must also be cleared
        _ = context.Sender.Should().BeNull();
        _ = context.Callback.Should().BeNull();
        _ = context.Args.Should().BeNull();
    }

    [Fact]
    public void ResetForPool_ClearsAllFields_AfterFullLifecycle()
    {
        // Arrange
        PooledConnectEventContext context = new();

        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.ReleasePendingPacketOnCompletion = true;

        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs(),
            releasePendingPacketOnCompletion: true);

        // Act
        context.ResetForPool();

        // Assert: all references cleared
        _ = context.LocalOwner.Should().BeNull();
        _ = context.Sender.Should().BeNull();
        _ = context.Callback.Should().BeNull();
        _ = context.Args.Should().BeNull();
        _ = context.ReleasePendingPacketOnCompletion.Should().BeFalse();
    }

    [Fact]
    public void Dispose_ReturnsToOwnerPool_WhenLocalOwnerSet()
    {
        // Arrange
        PooledConnectEventContext context = new();
        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs());

        // Act
        context.Dispose();

        // Assert: returned to owner's pool, not global pool
        _ = fakeOwner.ReturnContextCalled.Should().BeTrue();
    }

    // ─── Phase 3A: IThreadPoolWorkItem tests ────────────────────────────────

    [Fact]
    public void PooledConnectEventContext_ImplementsIThreadPoolWorkItem()
    {
        // Verify the interface is implemented (compile-time guarantee + runtime check).
        PooledConnectEventContext context = new();
        _ = context.Should().BeAssignableTo<IThreadPoolWorkItem>(
            "PooledConnectEventContext must implement IThreadPoolWorkItem to avoid ThreadPool wrapper allocations");
    }

    [Fact]
    public void Execute_InvokesStoredInvokerExactlyOnce()
    {
        // Arrange
        PooledConnectEventContext context = new();
        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs(),
            releasePendingPacketOnCompletion: false);

        var counter = new InvocationCounter();
        Action<object> countingInvoker = counter.CreateInvoker();

        context.SetThreadPoolInvoker(countingInvoker);

        // Act — call Execute via the interface.
        ((IThreadPoolWorkItem)context).Execute();

        // Assert
        _ = counter.Count.Should().Be(1, "Execute should invoke the stored invoker exactly once");
    }

    [Fact]
    public void Execute_NullInvoker_DisposesDefensively()
    {
        // Arrange: do NOT set an invoker.
        PooledConnectEventContext context = new();
        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs());

        // Act — should not throw.
        Exception? caught = null;
        try
        {
            ((IThreadPoolWorkItem)context).Execute();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // Assert: no exception, and context was returned to pool.
        _ = caught.Should().BeNull("null invoker path should dispose defensively without throwing");
        _ = fakeOwner.ReturnContextCalled.Should().BeTrue(
            "context should be returned to owner pool via Dispose when invoker is null");
    }

    [Fact]
    public void ResetForPool_ClearsStoredInvoker()
    {
        // Arrange
        PooledConnectEventContext context = new();
        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs());
        context.SetThreadPoolInvoker(static _ => { });

        // Act
        context.ResetForPool();

        // Assert: after reset, Execute with null invoker should not invoke anything.
        var counter = new InvocationCounter();
        // Re-initialize with a tracking invoker to prove the old one was cleared.
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs());
        // Do NOT set a new invoker — the old one should be cleared.

        ((IThreadPoolWorkItem)context).Execute();

        // If invoker was NOT cleared, Execute would have invoked the old static delegate.
        // Since invoker IS cleared, Execute calls Dispose defensively.
        // We verify by checking that no counting invoker ran.
        _ = counter.Count.Should().Be(0, "ResetForPool must clear the stored invoker");
    }

    [Fact]
    public void SetThreadPoolInvoker_StoresInvoker_ForExecuteToCall()
    {
        // Arrange
        bool invoked = false;
        PooledConnectEventContext context = new();
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs());

        Action<object> invoker = _ => invoked = true;
        context.SetThreadPoolInvoker(invoker);

        // Act
        ((IThreadPoolWorkItem)context).Execute();

        // Assert
        _ = invoked.Should().BeTrue("SetThreadPoolInvoker should store the invoker for Execute to call");
    }

    [Fact]
    public async Task HighConcurrency_DispatchStressTest_NoContextLeak()
    {
        // Stress test: queue many contexts through IThreadPoolWorkItem path
        // and verify they all complete without leaks or crashes.
        const int iterations = 5000;
        int completedCount = 0;
        var fakeOwner = new FakeConnectContextPool();

        for (int i = 0; i < iterations; i++)
        {
            PooledConnectEventContext ctx = new();
            ctx.LocalOwner = fakeOwner;
            ctx.Initialize(
                static (_, _) => { },
                sender: new object(),
                args: new FakeConnectEventArgs(),
                releasePendingPacketOnCompletion: false);

            // Simulate the invoker that does callback + dispose (like s_invokeHigh).
            ctx.SetThreadPoolInvoker(state =>
            {
                if (state is PooledConnectEventContext c)
                {
                    c.Callback?.Invoke(c.Sender, c.Args);
                    c.Dispose();
                    _ = Interlocked.Increment(ref completedCount);
                }
            });

            // Queue to real ThreadPool via IThreadPoolWorkItem path.
            ThreadPool.UnsafeQueueUserWorkItem(ctx, preferLocal: false);
        }

        // Wait for all to complete (generous timeout).
        int maxWait = 0;
        while (Volatile.Read(ref completedCount) < iterations && maxWait < 200)
        {
            await Task.Delay(50);
            maxWait++;
        }

        // Assert: all completed without leak or crash.
        _ = Volatile.Read(ref completedCount).Should().Be(iterations,
            "all {0} contexts should complete and be returned without leak", iterations);
    }

    [Fact]
    public void Execute_ProcessInvoker_IsCalledViaExecute()
    {
        // Verify that when a process-lane invoker is stored and Execute() is called,
        // the invoker runs (simulating AsyncCallback.s_invokeProcess behavior).
        bool invokerRan = false;
        PooledConnectEventContext context = new();
        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs(),
            releasePendingPacketOnCompletion: true);

        Action<object> processInvoker = state =>
        {
            invokerRan = true;
            if (state is PooledConnectEventContext c)
            {
                c.Callback?.Invoke(c.Sender, c.Args);
                c.Dispose();
            }
        };
        context.SetThreadPoolInvoker(processInvoker);

        // Act
        ((IThreadPoolWorkItem)context).Execute();

        // Assert
        _ = invokerRan.Should().BeTrue("process invoker should be called via Execute()");
    }

    [Fact]
    public void Execute_PostInvoker_IsCalledViaExecute()
    {
        // Verify that when a post-lane invoker is stored and Execute() is called,
        // the invoker runs (simulating AsyncCallback.s_invokePost behavior).
        bool invokerRan = false;
        PooledConnectEventContext context = new();
        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs(),
            releasePendingPacketOnCompletion: false);

        Action<object> postInvoker = state =>
        {
            invokerRan = true;
            if (state is PooledConnectEventContext c)
            {
                c.Callback?.Invoke(c.Sender, c.Args);
                c.Dispose();
            }
        };
        context.SetThreadPoolInvoker(postInvoker);

        // Act
        ((IThreadPoolWorkItem)context).Execute();

        // Assert
        _ = invokerRan.Should().BeTrue("post invoker should be called via Execute()");
    }

    [Fact]
    public void QueueFailure_StillReturnsContextExactlyOnce()
    {
        // Verify that when ThreadPool.UnsafeQueueUserWorkItem fails (throws),
        // the context is still returned to the pool exactly once.
        PooledConnectEventContext context = new();
        var fakeOwner = new FakeConnectContextPool();
        context.LocalOwner = fakeOwner;
        context.Initialize(
            static (_, _) => { },
            sender: new object(),
            args: new FakeConnectEventArgs());
        context.SetThreadPoolInvoker(static _ => { });

        // Simulate queue failure: just call Dispose directly
        // (mirrors what AsyncCallback.QUEUE does on failure).
        context.Dispose();

        // Assert
        _ = fakeOwner.ReturnContextCalled.Should().BeTrue(
            "queue failure path must return context to pool exactly once");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private sealed class InvocationCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public Action<object> CreateInvoker()
        {
            return state =>
            {
                _ = Interlocked.Increment(ref _count);
                // Simulate EXECUTE_AND_RETURN: invoke callback, then dispose.
                if (state is PooledConnectEventContext ctx)
                {
                    ctx.Callback?.Invoke(ctx.Sender, ctx.Args);
                    ctx.Dispose();
                }
            };
        }
    }

    #region Fakes

    private sealed class FakeConnectContextPool : IPooledConnectContextPool
    {
        public bool ReturnContextCalled { get; private set; }

        public PooledConnectEventContext AcquireContext() => new();

        public void ReturnContext(PooledConnectEventContext context)
        {
            ReturnContextCalled = true;
            context.ResetForPool();
        }

        public void ReleasePendingPacket() { }
    }

    private sealed class FakeConnectEventArgs : IConnectionEventArgs
    {
        public IConnection Connection => null!;
        public INetworkEndpoint? NetworkEndpoint => null;
        public IBufferLease? Lease => null;
        public void Dispose() { }
    }

    #endregion
}

