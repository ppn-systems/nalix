// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using Nalix.Environment.IO;

namespace Nalix.Environment.Tests.IO;

public sealed class DirectoriesTemporaryPathTests
{
    [Fact]
    public void TemporaryDirectory_WhenResolvedPathIsSharedSystemTemp_UsesNalixOwnedSubdirectory()
    {
        // TemporaryDirectory hardens its directory to 0700 and purges files older than the retention window.
        // Doing that to the shared /tmp (container default, or NALIX_TEMP_PATH=/tmp) would break and delete
        // other processes' files, so the resolved path must be a subdirectory owned by Nalix.
        MethodInfo resolve = typeof(Directories).GetMethod("RESOLVE_TEMP_PATH", BindingFlags.NonPublic | BindingFlags.Static)!;
        // The platform's shared temp root: /tmp (or $TMPDIR) on Unix, %TEMP% on Windows.
        string sharedTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string? previous = System.Environment.GetEnvironmentVariable("NALIX_TEMP_PATH");
        try
        {
            System.Environment.SetEnvironmentVariable("NALIX_TEMP_PATH", sharedTemp);

            string resolved = (string)resolve.Invoke(null, null)!;

            Assert.Equal(Path.Join(sharedTemp, "nalix"), resolved);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("NALIX_TEMP_PATH", previous);
        }
    }
}
