// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.Observability.Tests;

/// <summary>
/// Names of the xUnit collections used by this test assembly. Tests that touch process-wide state join one of
/// these so they never run concurrently with each other.
/// </summary>
public static class TestCollections
{
    /// <summary>Tests that replace the process-wide observability key or the shared report registry.</summary>
    public const string ObservabilityState = "ObservabilityStateTests";
}

[CollectionDefinition(TestCollections.ObservabilityState, DisableParallelization = true)]
public sealed class ObservabilityStateCollection;
