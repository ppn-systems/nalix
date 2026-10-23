using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Nalix.Network.Tests;

public sealed class SlidingWindowTests
{
    private static readonly Type s_slidingWindowType = ResolveSlidingWindowType();
    private static readonly MethodInfo s_tryCheckMethod =
        s_slidingWindowType.GetMethod("TryCheck", [typeof(ushort)])
        ?? throw new InvalidOperationException("Unable to resolve SlidingWindow.TryCheck(ushort).");

    [Fact]
    public void TryCheck_WrapAround_65535To0_AcceptsNewPackets()
    {
        object window = CreateWindow(windowSize: 1024);

        InvokeTryCheck(window, 65534).Should().BeTrue();
        InvokeTryCheck(window, 65535).Should().BeTrue();
        InvokeTryCheck(window, 0).Should().BeTrue();
        InvokeTryCheck(window, 1).Should().BeTrue();
    }

    [Fact]
    public void TryCheck_WrapAround_ReplayedPackets_AreRejected()
    {
        object window = CreateWindow(windowSize: 1024);

        InvokeTryCheck(window, 65535).Should().BeTrue();
        InvokeTryCheck(window, 0).Should().BeTrue();

        InvokeTryCheck(window, 65535).Should().BeFalse();
        InvokeTryCheck(window, 0).Should().BeFalse();
    }

    [Fact]
    public void TryCheck_WrapAround_OutOfOrderWithinWindow_AcceptsPacket()
    {
        object window = CreateWindow(windowSize: 1024);

        InvokeTryCheck(window, 65534).Should().BeTrue();
        InvokeTryCheck(window, 0).Should().BeTrue();
        InvokeTryCheck(window, 65535).Should().BeTrue();
    }

    [Fact]
    public void TryCheck_ExactlyAtWindowBoundary_IsAccepted_OneBeyond_IsRejectedAsTooOld()
    {
        object window = CreateWindow(windowSize: 1024);

        InvokeTryCheck(window, 2000).Should().BeTrue();

        // diff == windowSize - 1 is the oldest still-in-window sequence; must be accepted.
        InvokeTryCheck(window, (ushort)(2000 - 1023)).Should().BeTrue("diff=1023 is the last in-window slot for windowSize=1024");

        // diff == windowSize is one past the window; must be rejected as too old.
        InvokeTryCheck(window, (ushort)(2000 - 1024)).Should().BeFalse("diff=1024 equals windowSize and must be rejected as too old");
    }

    [Fact]
    public void TryCheck_OldPacketReplayedAfterHalfRingAdvance_IsRejectedAndDoesNotPoisonMaxSeen()
    {
        object window = CreateWindow(windowSize: 1024);

        // Legitimate client starts at seq = 100 and advances past half the 16-bit ring to seq = 35000.
        InvokeTryCheck(window, 100).Should().BeTrue();
        for (int seq = 500; seq <= 35000; seq += 500)
        {
            InvokeTryCheck(window, (ushort)seq).Should().BeTrue();
        }

        // Attacker replays captured packet from early in the session (seq = 100).
        // (ushort)(100 - 35000) == 30636 < 32768, which must NOT be accepted as a forward jump!
        InvokeTryCheck(window, 100).Should().BeFalse("an old packet replayed after >32768 legitimate packets must not be accepted as a forward jump");

        // Next legitimate packet (35001) must still be accepted.
        InvokeTryCheck(window, 35001).Should().BeTrue("legitimate traffic after a replay attempt must not be locked out");
    }

    /// <summary>
    /// Area 3 (rate limiting concurrency exactness, replay-window variant): 64 threads racing to
    /// mark a disjoint set of sequence numbers within the same window concurrently must all
    /// succeed exactly once each — the internal bitmap's lock must serialize marks without losing
    /// or double-counting any of them (seed drives per-thread start-delay jitter only).
    /// </summary>
    [Fact]
    [Trait("Category", "Stress")]
    public void TryCheck_ConcurrentDisjointSequenceNumbers_AllMarkedExactlyOnce()
    {
        const int seed = 20260704;
        const int threadCount = 64;
        object window = CreateWindow(windowSize: 1024);

        // Establish _maxSeen so all subsequent disjoint seqs fall within the window as "older but unseen".
        InvokeTryCheck(window, 1023).Should().BeTrue();

        System.Random rng = new(seed);
        int acceptedCount = 0;
        using Barrier barrier = new(threadCount);
        Thread[] threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            ushort seq = (ushort)i; // disjoint, all within [0,1023] window relative to maxSeen=1023
            int delayTicks = rng.Next(0, 5);
            threads[i] = new Thread(() =>
            {
                for (int spin = 0; spin < delayTicks; spin++)
                {
                    Thread.SpinWait(1);
                }
                barrier.SignalAndWait();
                if (InvokeTryCheck(window, seq))
                {
                    Interlocked.Increment(ref acceptedCount);
                }
            });
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        acceptedCount.Should().Be(threadCount, $"seed={seed}: all {threadCount} disjoint concurrent sequence numbers must be marked exactly once, never lost or double-rejected");
    }

    private static object CreateWindow(int windowSize)
        => Activator.CreateInstance(s_slidingWindowType, [windowSize])
           ?? throw new InvalidOperationException("Unable to instantiate SlidingWindow.");

    private static bool InvokeTryCheck(object window, ushort seq)
        => (bool)(s_tryCheckMethod.Invoke(window, [seq])
                  ?? throw new InvalidOperationException("SlidingWindow.TryCheck returned null."));

    private static Type ResolveSlidingWindowType()
        => Type.GetType("Nalix.Network.Internal.Security.SlidingWindow, Nalix.Network")
           ?? throw new InvalidOperationException("Unable to resolve Nalix.Network.Internal.Security.SlidingWindow.");
}















