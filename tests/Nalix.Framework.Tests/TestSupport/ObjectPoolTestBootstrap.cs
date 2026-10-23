// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Framework.Tests.TestSupport;

/// <summary>
/// Configures the process-wide <see cref="ObjectPoolManager"/> once for the whole test assembly.
/// </summary>
internal static class ObjectPoolTestBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        try
        {
            var config = new Nalix.Framework.Options.ObjectPoolOptions { EnableMetrics = false, EnableDiagnostics = false };
            ObjectPoolManager.Configure(new ObjectPoolManager(config));
        }
        catch (System.InvalidOperationException)
        {
        }
    }
}
