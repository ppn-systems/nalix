// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Extensions;
using Nalix.Environment.Extensions;
using Nalix.Framework.Extensions;

namespace Nalix.Framework.Tests.Extensions;

public sealed class ReportExtensionsTests
{
    [Fact]
    public void SaveReportToFile_WhenReportableIsNull_ThrowsArgumentNullException()
    {
        IReportable? reportable = null;

        Assert.Throws<ArgumentNullException>(() => reportable!.SaveReportToFile());
    }

    [Fact]
    public void SaveReportToFile_WhenCalled_CreatesFileWithReportContent()
    {
        FakeReportable reportable = new("hello-report");

        string path = reportable.SaveReportToFile("Prefix:*Value");

        try
        {
            Assert.True(File.Exists(path));
            Assert.Contains("prefix_", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("hello-report", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class FakeReportable(string text) : IReportable
    {
        private readonly string _text = text;

        public string GenerateReport() => _text;

#if NET10_0_OR_GREATER
        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
        {
            writer.WriteString("text", _text);
        }
#endif
    }
}
