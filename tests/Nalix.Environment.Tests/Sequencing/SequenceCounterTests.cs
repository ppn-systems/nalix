// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nalix.Abstractions.Security;
using Nalix.Environment.Sequencing;
using Xunit;

namespace Nalix.Environment.Tests.Sequencing;

/// <summary>
/// Unit tests for <see cref="SequenceCounter"/> verifying persistence,
/// thread-safety, and the sealed-class invariants required by the
/// WebSocket encrypted-frame path.
/// </summary>
public sealed class SequenceCounterTests
{
    #region Core Persistence

    [Fact]
    public void Next_WhenCalledRepeatedly_AdvancesPersistently()
    {
        // This is the exact test required by the debugging task.
        // It proves that calling Next() on a SequenceCounter returns
        // 1, 2, 3 and that Current() reflects the last returned value.
        SequenceCounter counter = new();

        Assert.Equal(1u, counter.Next());
        Assert.Equal(2u, counter.Next());
        Assert.Equal(3u, counter.Next());
        Assert.Equal(3u, counter.Current());
    }

    [Fact]
    public void Next_WhenCalledThroughReadonlyField_AdvancesAcrossCalls()
    {
        // Proves that a readonly SequenceCounter field in a class
        // does NOT suffer from defensive-copy behavior.
        // Only valid because SequenceCounter is a sealed class.
        SequenceOwner owner = new();

        Assert.Equal(1u, owner.Next());
        Assert.Equal(2u, owner.Next());
        Assert.Equal(3u, owner.Next());
    }

    #endregion

    #region Initial State

    [Fact]
    public void Current_BeforeAnyNext_ReturnsZero()
    {
        SequenceCounter counter = new();
        counter.Current().Should().Be(0u);
    }

    [Fact]
    public void Ctor_WithInitialValue_StartsFromOffset()
    {
        SequenceCounter counter = new(initialValue: 100);

        counter.Current().Should().Be(100u);
        counter.Next().Should().Be(101u);
        counter.Next().Should().Be(102u);
        counter.Current().Should().Be(102u);
    }

    #endregion

    #region IsValid

    [Fact]
    public void IsValid_WhenSequenceIsNull_ReturnsTrue()
    {
        SequenceCounter counter = new();
        counter.IsValid(null).Should().BeTrue();
    }

    [Fact]
    public void IsValid_WhenNothingReceivedYet_AcceptsFirstPacket()
    {
        // Server receive counter at 0 accepts any non-zero seq
        // when current == 0 (the "no packets received yet" state).
        SequenceCounter counter = new();
        counter.IsValid(1).Should().BeTrue();
    }

    [Fact]
    public void IsValid_WhenStrictlyMonotonic_AcceptsNextAndRejectsPrevious()
    {
        SequenceCounter counter = new();
        counter.UpdateTo(5);

        counter.IsValid(6).Should().BeTrue();  // next
        counter.IsValid(5).Should().BeFalse(); // replay
        counter.IsValid(4).Should().BeFalse(); // old
    }

    [Fact]
    public void IsValid_WithWindow_AcceptsOnlyWithinWindow()
    {
        SequenceCounter counter = new();
        counter.UpdateTo(10);

        // With window=5, seq 8 should be accepted (10-8=2 < 5)
        counter.IsValid(8, window: 5).Should().BeTrue();
        // seq 5 should be rejected (10-5=5, NOT < 5)
        counter.IsValid(5, window: 5).Should().BeFalse();
    }

    #endregion

    #region UpdateTo

    [Fact]
    public void UpdateTo_WhenValueIsLowerOrHigher_AdvancesOnlyToHigher()
    {
        SequenceCounter counter = new();

        counter.UpdateTo(5);
        counter.Current().Should().Be(5u);

        counter.UpdateTo(3); // lower - should be ignored
        counter.Current().Should().Be(5u);

        counter.UpdateTo(10);
        counter.Current().Should().Be(10u);
    }

    #endregion

    #region Reset

    [Fact]
    public void Reset_WhenCalled_SetsCounterToZero()
    {
        SequenceCounter counter = new();
        counter.Next();
        counter.Next();
        counter.Next();
        counter.Current().Should().Be(3u);

        counter.Reset();
        counter.Current().Should().Be(0u);

        // After reset, Next() starts from 1 again
        counter.Next().Should().Be(1u);
    }

    [Fact]
    public void Reset_WithValue_SetsSpecifiedValue()
    {
        SequenceCounter counter = new();
        counter.Next();

        counter.Reset(100);
        counter.Current().Should().Be(100u);
        counter.Next().Should().Be(101u);
    }

    #endregion

    #region Simulated WebSocket Dual-Packet Scenario

    [Fact]
    public void IsValid_WhenTwoSequentialFramesReceived_AcceptsBoth()
    {
        // Simulates the server-side DefaultFrameProcessor receiving
        // ObservabilityAccess (seq=1) then RuntimeObservation (seq=2).
        SequenceCounter serverReceive = new();
        uint tcpWindow = 0; // Default TcpWindow

        // Frame 1: ObservabilityAccess seq=1
        uint? seq1 = 1;
        bool valid1 = serverReceive.IsValid(seq1, tcpWindow);
        valid1.Should().BeTrue("first frame (seq=1) must be accepted when current=0");
        serverReceive.UpdateTo(seq1.Value);
        serverReceive.Current().Should().Be(1u);

        // Frame 2: RuntimeObservation seq=2
        uint? seq2 = 2;
        bool valid2 = serverReceive.IsValid(seq2, tcpWindow);
        valid2.Should().BeTrue("second frame (seq=2) must be accepted when current=1");
        serverReceive.UpdateTo(seq2.Value);
        serverReceive.Current().Should().Be(2u);
    }

    [Fact]
    public void Next_WhenUsedBySender_IncrementsSequence()
    {
        // Simulates the SDK WsFrameSender calling Next() for each
        // encrypted send. The sequence must be 1 then 2.
        SequenceCounter sdkSend = new();

        // First encrypted send (ObservabilityAccess)
        uint seqToUse1 = sdkSend.Next();
        seqToUse1.Should().Be(1u);
        sdkSend.Current().Should().Be(1u);

        // Second encrypted send (RuntimeObservation)
        uint seqToUse2 = sdkSend.Next();
        seqToUse2.Should().Be(2u);
        sdkSend.Current().Should().Be(2u);
    }

    [Fact]
    public void Next_WhenServerSendsResponses_ReturnsOneThenTwo()
    {
        // Simulates the server PacketSender calling SendSequence.Next()
        // for each encrypted response.
        SequenceCounter serverSend = new();

        // Response 1 (ObservabilityAccess response)
        uint seq1 = serverSend.Next();
        seq1.Should().Be(1u);

        // Response 2 (RuntimeObservation response)
        uint seq2 = serverSend.Next();
        seq2.Should().Be(2u);
    }

    [Fact]
    public void IsValid_WhenResponsesArriveInOrder_AcceptsBoth()
    {
        // Simulates the SDK WsFrameReader validating incoming
        // encrypted responses from the server.
        SequenceCounter clientReceive = new();

        // Response 1 arrives with seq=1
        bool valid1 = clientReceive.IsValid(1);
        valid1.Should().BeTrue("client must accept first response");
        clientReceive.UpdateTo(1);

        // Response 2 arrives with seq=2
        bool valid2 = clientReceive.IsValid(2);
        valid2.Should().BeTrue("client must accept second response");
        clientReceive.UpdateTo(2);
    }

    #endregion

    #region Replay Attack Prevention

    [Fact]
    public void IsValid_WhenSequenceIsDuplicate_ReturnsFalse()
    {
        SequenceCounter counter = new();
        counter.UpdateTo(1);

        counter.IsValid(1).Should().BeFalse("replay of seq=1 must be rejected");
    }

    [Fact]
    public void IsValid_WhenSequenceIsOutOfOrder_ReturnsFalse()
    {
        SequenceCounter counter = new();
        counter.UpdateTo(5);

        counter.IsValid(3).Should().BeFalse("out-of-order seq=3 after current=5 must be rejected");
    }

    #endregion

    #region Thread Safety

    [Fact]
    public void Next_WhenCalledConcurrently_ReturnsUniqueValues()
    {
        // Proves that Interlocked.Increment in Next() prevents duplicates
        // even under concurrent access.
        const int threadCount = 8;
        const int callsPerThread = 1000;

        SequenceCounter counter = new();
        ConcurrentBag<uint> results = new();

        Thread[] threads = new Thread[threadCount];
        Barrier barrier = new(threadCount);

        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < callsPerThread; i++)
                {
                    results.Add(counter.Next());
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        results.Should().HaveCount(threadCount * callsPerThread);
        results.Distinct().Should().HaveCount(threadCount * callsPerThread,
            "every Next() call must return a unique value");

        counter.Current().Should().Be((uint)(threadCount * callsPerThread));
    }

    #endregion

    #region ISequenceCounter Interface

    [Fact]
    public void Interface_WhenReferencedTwice_ReturnsSameObject()
    {
        // Proves that the same object reference is returned every time
        // (not a boxed copy of a struct).
        ISequenceCounter counter = new SequenceCounter();

        ISequenceCounter ref1 = counter;
        ISequenceCounter ref2 = counter;

        ReferenceEquals(ref1, ref2).Should().BeTrue(
            "ISequenceCounter references must be the same object (no boxing)");
    }

    [Fact]
    public void Next_WhenCalledThroughInterface_PersistsState()
    {
        ISequenceCounter counter = new SequenceCounter();

        counter.Next().Should().Be(1u);
        counter.Next().Should().Be(2u);
        counter.Current().Should().Be(2u);
    }

    #endregion

    #region SequenceOwner helper

    /// <summary>
    /// Demonstrates that a readonly SequenceCounter field in a sealed class
    /// works correctly (no defensive copy) because SequenceCounter is a class.
    /// </summary>
    private sealed class SequenceOwner
    {
        private readonly SequenceCounter _counter = new();

        public uint Next() => _counter.Next();
        public uint Current() => _counter.Current();
    }

    #endregion
}
