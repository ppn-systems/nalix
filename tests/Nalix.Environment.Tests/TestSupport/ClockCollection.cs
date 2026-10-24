// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.Environment.Tests.TestSupport;

/// <summary>
/// Serializes the tests that read the process-wide clock and compare it against the system time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClockCollection
{
    /// <summary>Collection name to pass to <c>[Collection]</c>.</summary>
    public const string Name = "ClockTests";
}
