// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;

namespace Nalix.Framework.Tests.TestSupport;

internal sealed class TestPoolable : IPoolStateTracked
{
    private int _poolState;

    public int Value { get; set; }

    public ref int PoolState => ref _poolState;

    public void ResetForPool() => this.Value = 0;
}
