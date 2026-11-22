// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class ConfigurationValidationAnalyzerTests
{
    private static string Source(string members) => $$"""
namespace Demo;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

public enum Mode { Fast, Safe }

public sealed class ServerOptions : ConfigurationLoader
{
    {{members}}
}
""";

    [Theory]
    [InlineData("[ValueRange(10, 1)] public int Port { get; set; }")]
    [InlineData("[ValueRange(1.5, 0.5)] public double Ratio { get; set; }")]
    [InlineData("[ValueRange(5L, -5L)] public long Offset { get; set; }")]
    [InlineData("[ValueRange(0, 10)] public string Name { get; set; } = \"\";")]
    [InlineData("[ValueRange(0, 10)] public bool Enabled { get; set; }")]
    [InlineData("[DurationRange(\"00:00:10\", \"00:00:01\")] public TimeSpan Delay { get; set; }")]
    [InlineData("[DurationRange(\"abc\", \"00:00:01\")] public TimeSpan Delay { get; set; }")]
    [InlineData("[DurationRange(\"00:00:01\", \"later\")] public TimeSpan Delay { get; set; }")]
    [InlineData("[DurationRange(\"00:00:01\", \"00:00:10\")] public int Delay { get; set; }")]
    [InlineData("[Length(3)] public int Size { get; set; }")]
    [InlineData("[AllowedEnum] public int Level { get; set; }")]
    [InlineData("[Required] public int Port { get; set; }")]
    [InlineData("[Required] public Mode Mode { get; set; }")]
    [InlineData("[Nalix.Abstractions.Validation.Required] public bool Enabled { get; set; }")]
    public async Task Analyze_MismatchedValidationAttribute_ReportsNalix118(string members)
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(Source(members), "NALIX118");
    }

    [Theory]
    [InlineData("[ValueRange(1, 10)] public int Port { get; set; }")]
    [InlineData("[ValueRange(1, 1)] public int Port { get; set; }")]
    [InlineData("[ValueRange(0.5, 2.5)] public double Ratio { get; set; }")]
    [InlineData("[ValueRange(0, 100)] public byte Percent { get; set; }")]
    [InlineData("[DurationRange(\"00:00:01\", \"1.00:00:00\")] public TimeSpan Delay { get; set; }")]
    [InlineData("[Length(3)] public string Name { get; set; } = \"\";")]
    [InlineData("[AllowedEnum] public Mode Mode { get; set; }")]
    [InlineData("[Required] public string Name { get; set; } = \"\";")]
    public async Task Analyze_MatchingValidationAttribute_ReportsNoDiagnostic(string members)
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(Source(members));
    }

    [Fact]
    public async Task Analyze_MismatchedAttributeOnIgnoredProperty_ReportsNoDiagnostic()
    {
        // The generator never binds or validates a [ConfiguredIgnore] property.
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(
            Source("[Nalix.Abstractions.ConfiguredIgnore] [ValueRange(10, 1)] public int Port { get; set; }"));
    }

    [Fact]
    public async Task Analyze_MismatchedAttributeOutsideConfigurationType_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Validation;

public sealed class NotConfiguration
{
    [ValueRange(10, 1)]
    public int Port { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_TwoMismatchedAttributes_ReportsEachOne()
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(
            Source("[ValueRange(10, 1)] public int Port { get; set; }\n    [AllowedEnum] public int Level { get; set; }"),
            "NALIX118",
            "NALIX118");
    }
}
