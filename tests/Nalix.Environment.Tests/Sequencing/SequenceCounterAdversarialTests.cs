// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Sequencing;
using Xunit;

namespace Nalix.Environment.Tests.Sequencing;

/// <summary>
/// Wraparound and resume-overflow behavior of <see cref="SequenceCounter"/>, not covered by
/// <see cref="SequenceCounterTests"/>: the explicit overflow guard in <see cref="SequenceCounter.Next"/>,
/// and the unchecked <c>uint</c> addition inside <see cref="SequenceCounter.ResumeFrom"/>.
/// </summary>
public sealed class SequenceCounterAdversarialTests
{
    [Fact]
    public void Next_WhenCounterWrapsToZero_ThrowsCipherException()
    {
        SequenceCounter counter = new(uint.MaxValue - 1);

        uint last = counter.Next();
        Assert.Equal(uint.MaxValue, last);

        _ = Assert.Throws<CipherException>(() => counter.Next());
    }

    [Fact]
    public void Next_AfterOverflowThrew_KeepsThrowingInsteadOfReissuingSequences()
    {
        SequenceCounter counter = new(uint.MaxValue - 1);

        _ = counter.Next();
        _ = Assert.Throws<CipherException>(() => counter.Next());
        _ = Assert.Throws<CipherException>(() => counter.Next());
        _ = Assert.Throws<CipherException>(() => counter.Next());
        Assert.Equal(uint.MaxValue, counter.Current());
    }

    /// <summary>
    /// Regression test: <see cref="SequenceCounter.ResumeFrom"/> must never wrap when
    /// <c>lastKnownSeq + safetyGap</c> exceeds <see cref="uint.MaxValue"/>. A wrap would resume
    /// the counter BELOW <c>lastKnownSeq</c> and let <c>Next()</c> reissue already-used sequence
    /// numbers (nonce reuse for the stream ciphers this counter feeds). The fixed implementation
    /// saturates at <see cref="uint.MaxValue"/>, so the next <c>Next()</c> call throws
    /// <see cref="CipherException"/> via the existing overflow policy, forcing key rotation.
    /// </summary>
    [Fact]
    public void ResumeFrom_WhenSumExceedsMaxValue_SaturatesInsteadOfWrapping()
    {
        SequenceCounter counter = new();
        const uint lastKnownSeq = uint.MaxValue - 500;
        const uint safetyGap = 1000; // default; sum exceeds uint.MaxValue by 499

        counter.ResumeFrom(lastKnownSeq, safetyGap);

        uint resumed = counter.Current();
        Assert.Equal(uint.MaxValue, resumed);

        // Saturated counter must fail loudly on the next issue, never silently reuse.
        _ = Assert.Throws<CipherException>(() => counter.Next());
    }

    [Fact]
    public void ResumeFrom_WhenWellBelowMaxValue_AdvancesNormally()
    {
        SequenceCounter counter = new();
        counter.ResumeFrom(100, 1000);

        Assert.Equal(1100u, counter.Current());
    }

    /// <summary>
    /// Boundary test for the pre-emptive overflow policy: senders check
    /// <see cref="SequenceCounter.IsApproachingOverflow"/> before reserving the next sequence number
    /// so a clean disconnect/key-rotation can happen before <see cref="SequenceCounter.Next"/>
    /// would throw <see cref="CipherException"/>.
    /// </summary>
    [Fact]
    public void IsApproachingOverflow_WhenCrossingMargin_ReturnsTrueOnlyWithinMargin()
    {
        SequenceCounter counter = new(uint.MaxValue - 1_000_001);
        Assert.False(counter.IsApproachingOverflow());

        _ = counter.Next(); // current = MaxValue - 1_000_000, exactly at the margin boundary
        Assert.True(counter.IsApproachingOverflow());
    }

    /// <summary>
    /// Regression test: a forward jump of 2^31 or more must reset the replay bitmap.
    /// Before the fix the delta was cast to a negative <c>int</c>, which skipped the
    /// <c>shift &gt;= 32</c> guard, so the bitmap marked sequences that were never received.
    /// </summary>
    [Fact]
    public void UpdateTo_WhenForwardJumpExceedsInt32_ResetsReplayBitmap()
    {
        SequenceCounter counter = new(5);
        counter.UpdateTo(5);

        uint jumped = 5u + 0x8000_0001u;
        counter.UpdateTo(jumped);

        Assert.Equal(jumped, counter.Current());
        Assert.True(counter.IsValid(jumped - 1, window: 32));
        Assert.False(counter.IsValid(jumped, window: 32));
    }

    [Fact]
    public void UpdateTo_WhenBackwardDistanceExceedsInt32_IsIgnored()
    {
        SequenceCounter counter = new(0x8000_0010u);
        counter.UpdateTo(0x8000_0010u);

        counter.UpdateTo(5);

        Assert.Equal(0x8000_0010u, counter.Current());
        Assert.True(counter.IsValid(0x8000_0010u - 1, window: 32));
    }
}
