// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;

namespace Nalix.Framework.Tests.TestSupport;

internal sealed class GenericPoolable<T> : IPoolable
{
    public void ResetForPool()
    {
    }
}
