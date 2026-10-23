// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nalix.Environment.IO;

namespace Nalix.Runtime.Tests;

internal static class TestAssemblySetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        string testDir = Path.Combine(Path.GetTempPath(), "NalixPipelineTests", Guid.NewGuid().ToString("N"));
        Directories.SetBasePathOverride(testDir);

        // Handlers that perform the handshake load the server identity from the configuration directory.
        string configDir = Directories.ConfigurationDirectory;
        _ = Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "certificate.private"),
            "0000000000000000000000000000000000000000000000000000000000000000");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(testDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }
}
