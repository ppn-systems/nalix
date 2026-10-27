// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Environment.Tests.Configuration.Binding;

public sealed class IniConfigTests : IDisposable
{
    private readonly string _path;

    public IniConfigTests()
    {
        _path = Path.GetTempFileName();
        File.WriteAllText(_path, "[Section]\n; Comment\nKey=Value\n");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void FilePath_WhenConstructedWithRelativePath_IsAbsolute()
    {
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), _path);

        using IniConfig config = new IniConfig(relative);

        Assert.True(Path.IsPathRooted(config.FilePath));
        Assert.Equal(Path.GetFullPath(_path), config.FilePath);
    }

    [Fact]
    public void Reload_WhenCalledRepeatedly_DoesNotAccumulateComments()
    {
        using IniConfig config = new IniConfig(_path);

        // Use reflection to check private _comments dictionary size
        var commentsField = typeof(IniConfig).GetField("_comments", BindingFlags.NonPublic | BindingFlags.Instance);
        var comments = (Dictionary<string, string>)commentsField.GetValue(config);

        Assert.Single(comments); // Initially has 1 comment

        // Reload multiple times
        for (int i = 0; i < 10; i++)
        {
            config.Reload();
        }

        // Should still be 1, not 11 or more
        Assert.Single(comments);
    }

    [Fact]
    public void WriteComment_WhenFileReopenedRepeatedly_DoesNotDuplicateComment()
    {
        // Start from a file that has the section but no key/comment yet.
        File.WriteAllText(_path, "[Section]\n");

        // Repeated open/write/flush cycles should not duplicate or "grow" the property comment.
        for (int i = 0; i < 5; i++)
        {
            using IniConfig config = new(_path);
            config.WriteComment("Section", "Key", "description");
            config.WriteValue("Section", "Key", $"Value{i}");
            config.Flush();
        }

        string content = File.ReadAllText(_path).Replace("\r\n", "\n");
        int occurrences = System.Text.RegularExpressions.Regex.Matches(
            content,
            @"^; Key: description$",
            System.Text.RegularExpressions.RegexOptions.Multiline).Count;

        Assert.Equal(1, occurrences);
    }
}
