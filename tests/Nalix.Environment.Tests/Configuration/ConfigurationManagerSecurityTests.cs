// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Configuration;
using Nalix.Environment.IO;
using Xunit;

namespace Nalix.Environment.Tests.Configuration;

public sealed class ConfigurationManagerSecurityTests : IDisposable
{
    private readonly string _testDirectory;

    public ConfigurationManagerSecurityTests()
    {
        _testDirectory = Path.Combine(
            Directories.ConfigurationDirectory,
            "SecurityTests_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void SetConfigFilePath_WhenPathMatchesDirectoryPrefix_ThrowsInternalErrorException()
    {
        // Setup:
        // Base:  C:\...\Config
        // Attack: C:\...\Config_Evil\hacker.ini

        string baseDir = Path.GetFullPath(Directories.ConfigurationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string evilDir = baseDir + "_Evil";
        string hackerIni = Path.Combine(evilDir, "hacker.ini");

        using ConfigurationManager manager = new();

        // This should fail because C:\...\Config_Evil does not start with C:\...\Config\ (with slash)
        Assert.Throws<InternalErrorException>(() => manager.SetConfigFilePath(hackerIni));
    }

    [Fact]
    public void SetConfigFilePath_WhenPathIsInsideDirectory_AcceptsPath()
    {
        using ConfigurationManager manager = new();
        string validPath = Path.Combine(Directories.ConfigurationDirectory, "valid_" + Guid.NewGuid().ToString("N") + ".ini");

        // Should NOT throw InternalErrorException (might throw FileNotFound or similar if we actually try to load,
        // but VALIDATE_CONFIG_PATH should pass)

        // SetConfigFilePath(validPath, autoReload: false) to avoid actually loading it if it doesn't exist
        manager.SetConfigFilePath(validPath, autoReload: false);

        Assert.Equal(Path.GetFullPath(validPath), manager.ConfigFilePath);
    }
}
