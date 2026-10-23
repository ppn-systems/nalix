// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.Framework.Tests.TestSupport;

/// <summary>
/// Serializes tests that toggle <see cref="CreationThrowingPoolable.ShouldThrowInNextAction"/>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThrowingPoolableCollection
{
    /// <summary>Collection name to pass to <c>[Collection]</c>.</summary>
    public const string Name = "ThrowingPoolable";
}
