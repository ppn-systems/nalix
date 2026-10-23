// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using Nalix.Analyzers.Generators;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Generators;

/// <summary>
/// Layer 1 (generator-output) tests for <see cref="InstanceGenerator"/>.
/// </summary>
public sealed class InstanceGeneratorTests
{
    private const string InjectableSource = """
        using Nalix.Abstractions.Injection;

        namespace GenHarness.Instance;

        [Injectable]
        public sealed class InjectableService
        {
            public InjectableService() { }
        }
        """;

    private const string SingletonSource = """
        using Nalix.Framework.Injection.DI;

        namespace GenHarness.Instance;

        public sealed class MySingleton : SingletonBase<MySingleton>
        {
            public MySingleton() { }
        }
        """;

    private const string SingletonMissingParameterlessCtorSource = """
        using Nalix.Framework.Injection.DI;

        namespace GenHarness.Instance;

        public sealed class BadSingleton : SingletonBase<BadSingleton>
        {
            public BadSingleton(int seed) { }
        }
        """;

    private const string AmbiguousCtorSource = """
        using Nalix.Abstractions.Injection;

        namespace GenHarness.Instance;

        [Injectable]
        public sealed class AmbiguousService
        {
            public AmbiguousService(object a) { }
            public AmbiguousService(string b) { }
        }
        """;

    [Fact]
    public void Generate_InjectableService_EmitsActivator()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new InstanceGenerator(), InjectableSource);

        Assert.Empty(result.GeneratorDiagnostics);

        (string HintName, string Text) source = result.GeneratedSources.Single();
        Assert.Contains("RegisterActivator", source.Text);
        Assert.Contains("InjectableService", source.Text);

        GeneratorDriverHarness.AssertNoCompileErrors(result);
    }

    [Fact]
    public void Generate_SingletonBaseSubclass_EmitsFactory()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new InstanceGenerator(), SingletonSource);

        Assert.Empty(result.GeneratorDiagnostics);

        (string HintName, string Text) source = result.GeneratedSources.Single();
        Assert.Contains("SingletonActivatorCache", source.Text);
        Assert.Contains("MySingleton", source.Text);

        GeneratorDriverHarness.AssertNoCompileErrors(result);
    }

    [Fact]
    public void Generate_SingletonWithoutParameterlessCtor_ReportsNalix065()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new InstanceGenerator(), SingletonMissingParameterlessCtorSource);

        string[] ids = [.. result.GeneratorDiagnostics.Select(static d => d.Id)];
        Assert.Contains("NALIX065", ids);
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generate_AmbiguousConstructors_ReportsNalix064()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new InstanceGenerator(), AmbiguousCtorSource);

        string[] ids = [.. result.GeneratorDiagnostics.Select(static d => d.Id)];
        Assert.Contains("NALIX064", ids);
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generate_SameSourceTwice_EmitsIdenticalOutput() =>
        GeneratorDriverHarness.AssertDeterministic(new InstanceGenerator(), InjectableSource);
}
