// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.Framework.Tests;

/// <summary>
/// Names of the xUnit collections used by this test assembly. Tests that touch process-wide state join one of
/// these so they never run concurrently with each other.
/// </summary>
public static class TestCollections
{
    /// <summary>Pool tests that reconfigure the process-wide pool managers.</summary>
    public const string SequentialPooling = "Sequential Pooling Tests";

    /// <summary>Tests that toggle object pool diagnostics.</summary>
    public const string ObjectPoolDiagnostics = "ObjectPoolDiagnostics";

    /// <summary>Tests that toggle pool return validation.</summary>
    public const string ReturnValidation = "ReturnValidation";

    /// <summary>TypePool tests that depend on exclusive pool state.</summary>
    public const string TypePoolPhase1 = "TypePoolPhase1";

    /// <summary>Tests that replace or advance the process-wide clock.</summary>
    public const string Clock = "ClockTests";
}

[CollectionDefinition(TestCollections.SequentialPooling, DisableParallelization = true)]
public sealed class SequentialPoolingCollection;

[CollectionDefinition(TestCollections.ObjectPoolDiagnostics, DisableParallelization = true)]
public sealed class ObjectPoolDiagnosticsCollection;

[CollectionDefinition(TestCollections.ReturnValidation, DisableParallelization = true)]
public sealed class ReturnValidationCollection;

[CollectionDefinition(TestCollections.TypePoolPhase1, DisableParallelization = true)]
public sealed class TypePoolPhase1Collection;

[CollectionDefinition(TestCollections.Clock, DisableParallelization = true)]
public sealed class ClockCollection;
