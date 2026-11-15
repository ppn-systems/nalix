// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Observability.Internal;

namespace Nalix.Observability.Tests.Internal;

/// <summary>
/// Verifies that the process-wide payload budget is clamped to its supported range.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class ObservabilityLimitsTests
{
    [Fact]
    public void PayloadBudget_WhenBelowMinimum_ClampsToMinimum()
    {
        int original = ObservabilityLimits.PayloadBudget;
        try
        {
            ObservabilityLimits.PayloadBudget = 1;

            Assert.Equal(ObservabilityLimits.MinPayloadBytes, ObservabilityLimits.PayloadBudget);
        }
        finally
        {
            ObservabilityLimits.PayloadBudget = original;
        }
    }

    [Fact]
    public void PayloadBudget_WhenAboveMaximum_ClampsToMaximum()
    {
        int original = ObservabilityLimits.PayloadBudget;
        try
        {
            ObservabilityLimits.PayloadBudget = int.MaxValue;

            Assert.Equal(ObservabilityLimits.MaxPayloadBytes, ObservabilityLimits.PayloadBudget);
        }
        finally
        {
            ObservabilityLimits.PayloadBudget = original;
        }
    }

    [Fact]
    public void PayloadBudget_WhenWithinRange_KeepsValue()
    {
        int original = ObservabilityLimits.PayloadBudget;
        try
        {
            ObservabilityLimits.PayloadBudget = 32 * 1024;

            Assert.Equal(32 * 1024, ObservabilityLimits.PayloadBudget);
        }
        finally
        {
            ObservabilityLimits.PayloadBudget = original;
        }
    }
}
