// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Configuration;
using Nalix.Environment.Configuration.Binding;
using Nalix.Environment.IO;
using Xunit;

namespace Nalix.Environment.Tests.Configuration;

/// <summary>
/// Provides comprehensive unit tests for the public API surface of <see cref="ConfigurationManager"/>.
/// </summary>
/// <remarks>
/// This test suite validates the following behaviors:
/// <list type="bullet">
/// <item><description>Configuration loading and binding from file.</description></item>
/// <item><description>Instance caching and reuse semantics.</description></item>
/// <item><description>File path validation and safety constraints.</description></item>
/// <item><description>Reload behavior (manual and automatic).</description></item>
/// <item><description>Cache invalidation (remove/clear).</description></item>
/// <item><description>Graceful handling of missing or invalid configuration files.</description></item>
/// </list>
///
/// Each test uses an isolated temporary directory to avoid cross-test interference.
/// </remarks>
public sealed partial class ConfigurationManagerTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly List<ConfigurationManager> _managers = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationManagerTests"/> class.
    /// </summary>
    public ConfigurationManagerTests()
    {
        _testDirectory = Path.Combine(
            Directories.ConfigurationDirectory,
            "ConfigurationManagerTests",
            Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(_testDirectory);
    }

    /// <summary>
    /// Releases all <see cref="ConfigurationManager"/> instances created during the test
    /// and attempts to clean up the temporary directory.
    /// </summary>
    /// <remarks>
    /// Cleanup is best-effort and may silently ignore exceptions,
    /// especially due to asynchronous file watcher disposal.
    /// </remarks>
    public void Dispose()
    {
        foreach (ConfigurationManager manager in _managers)
        {
            try
            {
                manager.Dispose();
            }
            catch
            {
                // Test cleanup should be best-effort.
            }
        }

        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // FileSystemWatcher cleanup can be asynchronous on some platforms.
        }
    }

    /// <summary>
    /// Verifies that calling <see cref="ConfigurationManager.Get{T}"/> multiple times
    /// returns the same cached instance.
    /// </summary>
    /// <remarks>
    /// This ensures that configuration objects are singleton per type within a manager instance,
    /// and no redundant allocations or rebindings occur.
    /// </remarks>
    [Fact]
    public void Get_WhenFileContainsValues_ReturnsInitializedConfiguration()
    {
        string filePath = this.WriteConfigFile(
            "appsettings.ini",
            """
            [Sample]
            Number = 42
            Message = hello
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);

        SampleConfig configuration = manager.Get<SampleConfig>();

        Assert.True(configuration.IsInitialized);
        Assert.Equal(42, configuration.Number);
        Assert.Equal("hello", configuration.Message);
        Assert.True(manager.IsLoaded<SampleConfig>());
        Assert.True(manager.ConfigFileExists);
    }

    /// <summary>
    /// Verifies that calling <see cref="ConfigurationManager.Get{T}"/> multiple times
    /// returns the same cached instance.
    /// </summary>
    /// <remarks>
    /// This ensures that configuration objects are singleton per type within a manager instance,
    /// and no redundant allocations or rebindings occur.
    /// </remarks>
    [Fact]
    public void Get_WhenCalledRepeatedly_ReturnsSameCachedInstance()
    {
        string filePath = this.WriteConfigFile(
            "cached.ini",
            """
            [Sample]
            Number = 7
            Message = cache
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);

        SampleConfig first = manager.Get<SampleConfig>();
        SampleConfig second = manager.Get<SampleConfig>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Get_WhenFileDoesNotExist_ReturnsDefaultValues()
    {
        string filePath = Path.Combine(_testDirectory, "missing.ini");

        using ConfigurationManager manager = this.CreateManager(filePath);

        Assert.False(manager.ConfigFileExists);

        SampleConfig configuration = manager.Get<SampleConfig>();

        Assert.NotNull(configuration);
        Assert.True(configuration.IsInitialized);
        Assert.Equal(0, configuration.Number);
        Assert.Equal(string.Empty, configuration.Message);
        Assert.True(manager.IsLoaded<SampleConfig>());
    }

    [Fact]
    public void Get_WithPathOverload_UsesProvidedConfigurationFile()
    {
        string filePath = this.WriteConfigFile(
            "overload.ini",
            """
            [Sample]
            Number = 15
            Message = overload
            """);

        using ConfigurationManager manager = this.CreateManager();

        SampleConfig configuration = manager.Get<SampleConfig>(filePath);

        Assert.Equal(Path.GetFullPath(filePath), manager.ConfigFilePath);
        Assert.Equal(15, configuration.Number);
        Assert.Equal("overload", configuration.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetConfigFilePath_WhenPathIsNullOrWhitespace_ThrowsArgumentException(string? path)
    {
        using ConfigurationManager manager = this.CreateManager();

        _ = Assert.Throws<ArgumentException>(() => manager.SetConfigFilePath(path!));
    }

    [Fact]
    public void SetConfigFilePath_WhenPathIsOutsideConfigurationDirectory_ThrowsInternalErrorException()
    {
        using ConfigurationManager manager = this.CreateManager();
        string outsidePath = Path.Combine(Path.GetTempPath(), $"outside_{Guid.NewGuid():N}.ini");

        _ = Assert.Throws<InternalErrorException>(() => manager.SetConfigFilePath(outsidePath));
    }

    [Fact]
    public void SetConfigFilePath_WhenSwitchingFiles_PreservesCachedState()
    {
        string firstPath = this.WriteConfigFile(
            "first.ini",
            """
            [Sample]
            Number = 1
            """);

        string secondPath = this.WriteConfigFile(
            "second.ini",
            """
            [Sample]
            Number = 2
            """);

        using ConfigurationManager manager = this.CreateManager(firstPath);
        SampleConfig config1 = manager.Get<SampleConfig>();
        Assert.Equal(1, config1.Number);

        manager.SetConfigFilePath(secondPath);
        SampleConfig config2 = manager.Get<SampleConfig>();
        Assert.Equal(2, config2.Number);
        Assert.NotSame(config1, config2);

        // Switch back to first - should be instantaneous (L1 Cache hit)
        manager.SetConfigFilePath(firstPath);
        SampleConfig config1_again = manager.Get<SampleConfig>();
        Assert.Same(config1, config1_again);
        Assert.Equal(1, config1_again.Number);
    }

    [Fact]
    public void SetConfigFilePath_WhenAutoReloadIsEnabled_UpdatesContextTimestamp()
    {
        string firstPath = this.WriteConfigFile(
            "auto-first.ini",
            """
            [Sample]
            Number = 10
            """);

        string secondPath = this.WriteConfigFile(
            "auto-second.ini",
            """
            [Sample]
            Number = 20
            """);

        using ConfigurationManager manager = this.CreateManager(firstPath);
        _ = manager.Get<SampleConfig>();

        DateTime beforeReload = manager.LastReloadTime;
        manager.SetConfigFilePath(secondPath, autoReload: true);

        Assert.Equal(Path.GetFullPath(secondPath), manager.ConfigFilePath);
        Assert.True(manager.LastReloadTime >= beforeReload);
    }

    /// <summary>
    /// Verifies that <see cref="ConfigurationManager.ReloadAll"/> reloads configuration data
    /// when the underlying file content changes.
    /// </summary>
    /// <remarks>
    /// This test ensures:
    /// <list type="bullet">
    /// <item><description>Existing instances are updated in-place (no new allocation).</description></item>
    /// <item><description>New values are correctly rebound from disk.</description></item>
    /// <item><description><see cref="ConfigurationManager.LastReloadTime"/> is updated.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void ReloadAll_WhenFileChanges_ReloadsConfigurationsAndUpdatesTimestamp()
    {
        string filePath = this.WriteConfigFile(
            "reload.ini",
            """
            [Sample]
            Number = 5
            Message = initial
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);
        SampleConfig configuration = manager.Get<SampleConfig>();
        DateTime beforeReload = manager.LastReloadTime;

        System.Threading.Thread.Sleep(20);

        File.WriteAllText(
            filePath,
            """
            [Sample]
            Number = 99
            Message = updated
            """);

        manager.ReloadAll();

        Assert.Equal(99, configuration.Number);
        Assert.Equal("updated", configuration.Message);
        Assert.True(manager.LastReloadTime > beforeReload);
    }

    [Fact]
    public void Remove_WhenConfigurationWasLoaded_RemovesItFromCache()
    {
        string filePath = this.WriteConfigFile(
            "remove.ini",
            """
            [Sample]
            Number = 8
            Message = remove
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);
        SampleConfig first = manager.Get<SampleConfig>();

        bool removed = manager.Remove<SampleConfig>();
        SampleConfig second = manager.Get<SampleConfig>();

        Assert.True(removed);
        Assert.NotSame(first, second);
        Assert.True(manager.IsLoaded<SampleConfig>());
    }

    [Fact]
    public void Remove_WhenConfigurationWasNotLoaded_ReturnsFalse()
    {
        using ConfigurationManager manager = this.CreateManager();

        bool removed = manager.Remove<SampleConfig>();

        Assert.False(removed);
    }

    [Fact]
    public void ClearAll_WhenConfigurationsWereLoaded_RemovesAllCachedInstances()
    {
        string filePath = this.WriteConfigFile(
            "clear.ini",
            """
            [Sample]
            Number = 3
            Message = clear
            [Another]
            Enabled = true
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);
        SampleConfig sample = manager.Get<SampleConfig>();
        AnotherConfig another = manager.Get<AnotherConfig>();

        manager.ClearAll();

        Assert.False(manager.IsLoaded<SampleConfig>());
        Assert.False(manager.IsLoaded<AnotherConfig>());

        SampleConfig reloadedSample = manager.Get<SampleConfig>();
        AnotherConfig reloadedAnother = manager.Get<AnotherConfig>();

        Assert.NotSame(sample, reloadedSample);
        Assert.NotSame(another, reloadedAnother);
    }

    [Fact]
    public void Flush_WhenConfigurationNotCreated_DoesNotThrow()
    {
        string filePath = Path.Combine(_testDirectory, "flush.ini");

        using ConfigurationManager manager = this.CreateManager(filePath);

        Exception? exception = Record.Exception(manager.Flush);

        Assert.Null(exception);
    }

    [Fact]
    public void Flush_WhenInitializationAddsMissingComments_PersistsCommentsToFile()
    {
        string filePath = this.WriteConfigFile(
            "flush-comments.ini",
            """
            [CommentedSample]
            Number = 9
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);
        _ = manager.Get<CommentedSampleConfig>();

        manager.Flush();

        string content = File.ReadAllText(filePath);
        Assert.Contains("; section-comment", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Flush_WhenSectionExistsWithoutComment_AddsSectionComment()
    {
        string filePath = this.WriteConfigFile(
            "flush-section-comment.ini",
            """
            [CommentedSample]
            Number = 1
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);

        _ = manager.Get<CommentedSampleConfig>();
        manager.Flush();

        string content = File.ReadAllText(filePath);
        Assert.Contains("; section-comment", content, StringComparison.Ordinal);
        Assert.Contains("Number", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Clone_WhenConfigurationInitialized_CopiesValuesAndMetadata()
    {
        string filePath = this.WriteConfigFile(
            "clone.ini",
            """
            [Sample]
            Number = 73
            Message = cloned
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);
        SampleConfig original = manager.Get<SampleConfig>();

        SampleConfig clone = original.Clone<SampleConfig>();

        Assert.NotSame(original, clone);
        Assert.True(clone.IsInitialized);
        Assert.Equal(original.Number, clone.Number);
        Assert.Equal(original.Message, clone.Message);
        Assert.Equal(original.LastInitializationTime, clone.LastInitializationTime);
    }

    [Fact]
    public void Get_WhenFileContainsEnumValues_BindsEnumAndNullableEnum()
    {
        string filePath = this.WriteConfigFile(
            "enum.ini",
            """
            [EnumSample]
            Mode = Advanced
            OptionalMode = 2
            """);

        using ConfigurationManager manager = this.CreateManager(filePath);

        EnumSampleConfig configuration = manager.Get<EnumSampleConfig>();

        Assert.Equal(SampleMode.Advanced, configuration.Mode);
        Assert.Equal(SampleMode.Legacy, configuration.OptionalMode);
    }

    private ConfigurationManager CreateManager(string? filePath = null)
    {
        ConfigurationManager manager = new();
        _managers.Add(manager);

        if (filePath is not null)
        {
            manager.SetConfigFilePath(filePath, autoReload: true);
        }

        return manager;
    }

    private string WriteConfigFile(string fileName, string content)
    {
        string filePath = Path.Combine(_testDirectory, fileName);
        File.WriteAllText(filePath, content.ReplaceLineEndings(System.Environment.NewLine));
        return filePath;
    }

    /// <summary>
    /// Represents a sample configuration used to validate scalar value binding.
    /// </summary>
    /// <remarks>
    /// Bound from the <c>[Sample]</c> section in the configuration file.
    /// </remarks>
    public sealed partial class SampleConfig : ConfigurationLoader
    {
        /// <summary>
        /// Gets or sets the numeric value used by the test configuration.
        /// </summary>
        public int Number { get; set; }

        /// <summary>
        /// Gets or sets the text value used by the test configuration.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents an additional configuration used to validate cache clearing behavior.
    /// </summary>
    /// <remarks>
    /// Used alongside <see cref="SampleConfig"/> to ensure multiple types are handled correctly.
    /// </remarks>
    public sealed partial class AnotherConfig : ConfigurationLoader
    {
        /// <summary>
        /// Gets or sets a value indicating whether the configuration is enabled.
        /// </summary>
        public bool Enabled { get; set; }
    }

    [IniComment("section-comment")]
    public sealed partial class CommentedSampleConfig : ConfigurationLoader
    {
        [IniComment("number-comment")]
        public int Number { get; set; }
    }

    public sealed partial class EnumSampleConfig : ConfigurationLoader
    {
        public SampleMode Mode { get; set; }
        public SampleMode? OptionalMode { get; set; }
    }

    public enum SampleMode
    {
        Basic = 0,
        Advanced = 1,
        Legacy = 2
    }
}
