// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using Nalix.Abstractions.Primitives;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Observability;

namespace Nalix.Integration.Tests.Observability;

/// <summary>
/// Verifies how <see cref="ObservabilityKey"/> parses, creates and lazily loads its key file.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class ObservabilityKeyTests : IDisposable
{
    private readonly ObservabilityKeyScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void Load_WhenFileHasCommentsAndBlankLines_UsesFirstValidKey()
    {
        Bytes32 key = new(RandomNumberGenerator.GetBytes(Bytes32.Size));
        string path = _scope.NewPath();
        File.WriteAllText(path, $"# comment\n\n{ObservabilityKeyScope.ToHex(key)}\n");

        ObservabilityKey.Load(path);

        Assert.Equal(key, ObservabilityKey.Current);
    }

    [Fact]
    public void Load_WhenFileIsMissing_CreatesFileWithGeneratedKey()
    {
        string path = _scope.NewPath();
        Assert.False(File.Exists(path));

        ObservabilityKey.Load(path);

        Assert.True(File.Exists(path));
        string stored = File.ReadAllText(path).Trim();
        Assert.Matches("^[0-9a-f]{64}$", stored);
        Assert.Equal(stored, ObservabilityKeyScope.ToHex(ObservabilityKey.Current));
        Assert.False(ObservabilityKey.Current.IsZero);
    }

    [Fact]
    public void Load_WhenFileHasInvalidKey_GeneratesFreshKey()
    {
        string path = _scope.NewPath();
        File.WriteAllText(path, "zz\n");

        ObservabilityKey.Load(path);
        Bytes32 first = ObservabilityKey.Current;

        ObservabilityKey.Load(path);
        Bytes32 second = ObservabilityKey.Current;

        Assert.False(first.IsZero);
        Assert.False(second.IsZero);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_WhenPathIsBlank_ThrowsArgumentException(string path)
    {
        _ = Assert.Throws<ArgumentException>(() => ObservabilityKey.Load(path));
    }

    [Fact]
    public void Current_WhenNotLoaded_LoadsDefaultPath()
    {
        ObservabilityKeyScope.ResetLazyLoad();

        Bytes32 current = ObservabilityKey.Current;

        string path = ObservabilityKey.DefaultPath;
        Assert.True(File.Exists(path));
        string? storedLine = File.ReadLines(path)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.Length > 0 && line[0] != '#');
        Assert.Equal(ObservabilityKeyScope.ToHex(current), storedLine);
    }
}
