// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nalix.Environment.IO;
using Xunit;

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass, MaxParallelThreads = 4)]

namespace Nalix.Framework.Tests;

internal static class TestAssemblySetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Redirect all application directories to a temporary location to ensure test isolation
        // and avoid collisions with system-wide configuration or data directories.
        string testDir = Path.Combine(Path.GetTempPath(), "NalixTests", Guid.NewGuid().ToString("N"));
        Directories.SetBasePathOverride(testDir);
    }
}
