// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using Nalix.Abstractions.Primitives;
using Nalix.Runtime.Security;
using Xunit;

namespace Nalix.Runtime.Tests.Security;

public sealed class FileCertificateStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public FileCertificateStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "NalixStoreTests_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Ignore clean up errors
        }
    }

    [Fact]
    public void SaveAndLoad_WithKeyPair_RoundTripsAndSetsSecureUnixPermissions()
    {
        FileCertificateStore store = new();
        string path = Path.Combine(_tempDirectory, "test_identity.key");

        Bytes32 privateKey = Bytes32.Parse("1111111111111111111111111111111111111111111111111111111111111111");
        Bytes32 publicKey = Bytes32.Parse("2222222222222222222222222222222222222222222222222222222222222222");

        store.Save(path, privateKey, publicKey);

        // Verify loaded private key matches
        Bytes32 loaded = store.Load(path);
        Assert.Equal(privateKey, loaded);

        // Verify public key file exists
        string publicPath = Path.Combine(_tempDirectory, "test_identity.public");
        Assert.True(File.Exists(publicPath));

        // On Unix, verify permissions are 0600 (UserRead | UserWrite)
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode privateMode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, privateMode);
        }
    }
}

