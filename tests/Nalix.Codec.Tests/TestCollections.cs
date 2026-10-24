// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.Codec.Tests;

/// <summary>
/// Names of the xUnit collections used by this test assembly. Tests that touch process-wide state join one of
/// these so they never run concurrently with each other.
/// </summary>
public static class TestCollections
{
    /// <summary>Tests that inspect pooled buffer contents after disposal.</summary>
    public const string PooledBufferScrub = "PooledBufferScrub";
}

[CollectionDefinition(TestCollections.PooledBufferScrub, DisableParallelization = true)]
public sealed class PooledBufferScrubCollection;
