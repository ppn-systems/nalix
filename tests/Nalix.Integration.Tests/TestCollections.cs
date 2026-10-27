// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.Integration.Tests;

/// <summary>
/// Names of the xUnit collections used by this test assembly. Tests that touch process-wide state join one of
/// these so they never run concurrently with each other.
/// </summary>
public static class TestCollections
{
    /// <summary>Tests that read or mutate the shared network option instances in ConfigurationManager.</summary>
    public const string NetworkConfig = "NetworkConfigTests";

    /// <summary>Tests that replace the process-wide observability key or the shared report registry.</summary>
    public const string ObservabilityState = "ObservabilityStateTests";
}

[CollectionDefinition(TestCollections.NetworkConfig)]
public sealed class NetworkConfigCollection;

[CollectionDefinition(TestCollections.ObservabilityState, DisableParallelization = true)]
public sealed class ObservabilityStateCollection;
