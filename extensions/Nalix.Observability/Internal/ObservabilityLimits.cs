// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Observability.Internal;

/// <summary>
/// Process-wide response limits configured by <c>UseObservability</c>.
/// </summary>
internal static class ObservabilityLimits
{
    public const int MinPayloadBytes = 8 * 1024;
    public const int MaxPayloadBytes = 1024 * 1024;

    private static int s_payloadBudget = 96 * 1024;

    /// <summary>Gets or sets the largest JSON payload a response may carry.</summary>
    public static int PayloadBudget
    {
        get => Volatile.Read(ref s_payloadBudget);
        set => Volatile.Write(ref s_payloadBudget, Math.Clamp(value, MinPayloadBytes, MaxPayloadBytes));
    }
}
