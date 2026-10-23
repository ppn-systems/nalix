// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using Nalix.Environment.IO;
using Xunit;

namespace Nalix.Environment.Tests.IO;

public sealed class DirectoriesHardeningTests : IDisposable
{
    private readonly string _testDir;

    public DirectoriesHardeningTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "NalixTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void DeleteOldFiles_WhenOneTimestampIsRecent_KeepsFile()
    {
        string oldFilePath = Path.Combine(_testDir, "old.txt");
        File.WriteAllText(oldFilePath, "old content");

        // Set both to very old
        DateTime veryOld = DateTime.UtcNow.AddDays(-10);
        File.SetCreationTimeUtc(oldFilePath, veryOld);
        File.SetLastWriteTimeUtc(oldFilePath, veryOld);

        string partialOldFilePath = Path.Combine(_testDir, "partial.txt");
        File.WriteAllText(partialOldFilePath, "partial content");

        // Set only write time to old, creation is new
        File.SetLastWriteTimeUtc(partialOldFilePath, veryOld);
        File.SetCreationTimeUtc(partialOldFilePath, DateTime.UtcNow);

        // Run cleanup for files older than 1 day
        int deleted = Directories.DeleteOldFiles(_testDir, TimeSpan.FromDays(1));

        // Only the truly old file should be deleted
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldFilePath));
        Assert.True(File.Exists(partialOldFilePath));
    }

    [Fact]
    public void CanAccessAllDirectories_WhenCalled_ReturnsBool()
    {
        bool result = Directories.CanAccessAllDirectories();
        Assert.IsType<bool>(result);
    }
}
