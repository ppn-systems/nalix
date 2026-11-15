// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;

namespace Nalix.Tunneling.Tests.TestSupport;

/// <summary>
/// Configures the process-wide object pool before any test runs. Pooled packets, scopes and object maps
/// fail with "used before the host configured it" otherwise.
/// </summary>
internal static class PoolBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            ObjectPoolManager.Configure(new ObjectPoolManager(new ObjectPoolOptions { EnableMetrics = false, EnableDiagnostics = false }));
        }
        catch (System.InvalidOperationException)
        {
            // Already configured in this process; keep the existing pool.
        }
    }
}
