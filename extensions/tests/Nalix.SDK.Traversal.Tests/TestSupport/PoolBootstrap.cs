// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using Nalix.Codec.DataFrames;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;

namespace Nalix.SDK.Traversal.Tests.TestSupport;

/// <summary>
/// Configures the process-wide object pool and the packet registry before any test runs.
/// </summary>
internal static class PoolBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            ObjectPoolManager.Configure(new ObjectPoolManager(new ObjectPoolOptions { EnableDiagnostics = false }));
        }
        catch (System.InvalidOperationException)
        {
            // Already configured in this process; keep the existing pool.
        }

        PacketRegistry.Build();
    }
}
