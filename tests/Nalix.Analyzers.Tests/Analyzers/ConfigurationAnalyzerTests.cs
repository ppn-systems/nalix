// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class ConfigurationAnalyzerTests
{
    [Fact]
    public async Task Analyze_SupportedConfigurationProperties_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using System;
using Nalix.Environment.Configuration.Binding;

public enum Mode
{
    Off,
    On
}

public sealed class DemoOptions : ConfigurationLoader
{
    public int Port { get; set; }
    public string Host { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public TimeSpan Timeout { get; set; }
    public DateTime LastUpdated { get; set; }
    public Mode CurrentMode { get; set; }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task Analyze_UnsupportedPropertyWithConfigurationIgnore_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using System.Collections.Generic;
using Nalix.Abstractions;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    [ConfiguredIgnore]
    public List<int> Values { get; set; } = [];
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task Analyze_GetterOnlyAndPrivateSetterProperties_ReportsNalix024()
    {
        const string source = """
namespace Demo;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    public int ReadOnlyPort { get; }
    public int SecretPort { get; private set; }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX024",
            "NALIX024");
    }

    [Fact]
    public async Task Analyze_UnsupportedPropertyType_ReportsNalix023()
    {
        const string source = """
namespace Demo;
using System.Collections.Generic;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoConfig : ConfigurationLoader
{
    public List<int> Values { get; set; } = new();
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX023");
    }

    [Fact]
    public async Task Analyze_PropertyWithoutPublicSetter_ReportsNalix024()
    {
        const string source = """
namespace Demo;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoConfig : ConfigurationLoader
{
    public int Port { get; private set; } = 7777;
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX024");
    }
}
