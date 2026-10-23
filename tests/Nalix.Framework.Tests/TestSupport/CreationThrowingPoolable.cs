// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;

namespace Nalix.Framework.Tests.TestSupport;

/// <summary>
/// A poolable whose constructor throws while <see cref="ShouldThrowInNextAction"/> is set. The flag is process-wide,
/// so every test that sets it must join <see cref="ThrowingPoolableCollection"/>.
/// </summary>
public sealed class CreationThrowingPoolable : IPoolable
{
    public static bool ShouldThrowInNextAction = false;

    public CreationThrowingPoolable()
    {
        if (ShouldThrowInNextAction)
        {
            throw new InvalidOperationException("Simulated OOM");
        }
    }

    public void ResetForPool()
    {
    }
}
