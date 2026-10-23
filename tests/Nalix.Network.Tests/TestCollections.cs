// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.Network.Tests;

/// <summary>
/// Names of the xUnit collections used by this test assembly. Tests that touch process-wide state join one of
/// these so they never run concurrently with each other.
/// </summary>
public static class TestCollections
{
    /// <summary>Tests that read or mutate the shared network option instances in ConfigurationManager.</summary>
    public const string NetworkConfig = "NetworkConfigTests";

    /// <summary>Tests that observe the process-wide AsyncCallback dispatcher statistics.</summary>
    public const string AsyncCallback = "AsyncCallback serial";

    /// <summary>Dispatch busy-spin regression tests that need the whole machine to themselves.</summary>
    public const string DispatchChurn = "Dispatch churn serial";
}

[CollectionDefinition(TestCollections.NetworkConfig)]
public sealed class NetworkConfigCollection;

[CollectionDefinition(TestCollections.AsyncCallback, DisableParallelization = true)]
public sealed class AsyncCallbackCollection;

[CollectionDefinition(TestCollections.DispatchChurn, DisableParallelization = true)]
public sealed class DispatchChurnCollection;
