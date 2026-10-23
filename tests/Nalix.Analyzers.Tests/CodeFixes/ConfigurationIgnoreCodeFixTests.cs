// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class ConfigurationIgnoreCodeFixTests
{
    [Fact]
    public async Task Fix_UnsupportedConfigurationProperty_AddsConfigurationIgnore()
    {
        const string source = """
namespace Demo;
using System.Collections.Generic;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    public List<int> Values { get; set; } = [];
}
""";

        const string fixedSource = """
namespace Demo;
using System.Collections.Generic;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    [Nalix.Abstractions.ConfiguredIgnore]
    public List<int> Values { get; set; } = [];
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX023",
            actionIndex: 0,
            expectedTitle: "Add [ConfiguredIgnore]",
            expectedEquivalenceKey: "Nalix.Configuration.ConfiguredIgnore.Add");
    }

    [Fact]
    public async Task Fix_GetterOnlyProperty_AddsConfigurationIgnore()
    {
        const string source = """
namespace Demo;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    public int ReadOnlyPort { get; }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    [Nalix.Abstractions.ConfiguredIgnore]
    public int ReadOnlyPort { get; }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX024",
            actionIndex: 0,
            expectedTitle: "Add [ConfiguredIgnore]",
            expectedEquivalenceKey: "Nalix.Configuration.ConfiguredIgnore.Add");
    }

    [Fact]
    public async Task Fix_GetterOnlyProperty_AddsPublicSetter()
    {
        const string source = """
namespace Demo;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    public int ReadOnlyPort { get; }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    public int ReadOnlyPort { get; set; }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX024",
            actionIndex: 1,
            expectedTitle: "Make setter public",
            expectedEquivalenceKey: "Nalix.Configuration.Setter.MakePublic");
    }

    [Fact]
    public async Task Fix_PrivateSetterProperty_MakesSetterPublic()
    {
        const string source = """
namespace Demo;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    public int SecretPort { get; private set; }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Environment.Configuration.Binding;

public sealed class DemoOptions : ConfigurationLoader
{
    public int SecretPort { get; set; }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX024",
            actionIndex: 1,
            expectedTitle: "Make setter public",
            expectedEquivalenceKey: "Nalix.Configuration.Setter.MakePublic");
    }
}
