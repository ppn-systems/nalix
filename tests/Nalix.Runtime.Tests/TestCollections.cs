// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.Runtime.Tests;

/// <summary>
/// Names of the xUnit collections used by this test assembly. Tests that touch process-wide state join one of
/// these so they never run concurrently with each other.
/// </summary>
public static class TestCollections
{
    /// <summary>Tests that register services in <c>InstanceManager</c> or <c>ScopedServiceRegistry.Instance</c>.</summary>
    public const string ProcessWideServices = "ProcessWideServicesTests";
}

[CollectionDefinition(TestCollections.ProcessWideServices, DisableParallelization = true)]
public sealed class ProcessWideServicesCollection;
