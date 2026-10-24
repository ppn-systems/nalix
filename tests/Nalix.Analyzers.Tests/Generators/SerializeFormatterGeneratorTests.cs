// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using Nalix.Analyzers.Generators;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Generators;

/// <summary>
/// Layer 1 (generator-output) tests for <see cref="SerializeFormatterGenerator"/>.
/// </summary>
public sealed class SerializeFormatterGeneratorTests
{
    private const string StructSource = """
        using Nalix.Abstractions.Serialization;

        namespace GenHarness.Formatter;

        [GenerateFormatter]
        public struct SchemaStruct
        {
            [SerializeOrder(0)]
            public int Id;

            [SerializeOrder(1)]
            public byte Flag;
        }
        """;

    private const string ClassMissingCreateSource = """
        using Nalix.Abstractions.Serialization;

        namespace GenHarness.Formatter;

        [GenerateFormatter]
        public sealed class NoCreateClass
        {
            [SerializeOrder(0)]
            public int Id { get; set; }
        }
        """;

    [Fact]
    public void Generate_SerializableStruct_EmitsFormatter()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new SerializeFormatterGenerator(), StructSource);

        Assert.Empty(result.GeneratorDiagnostics);

        (string HintName, string Text) formatterSource = result.GeneratedSources
            .Single(static s => s.HintName.Contains("SchemaStructFormatter"));
        Assert.Contains("IFormatter<", formatterSource.Text);

        GeneratorDriverHarness.AssertNoCompileErrors(result);
    }

    [Fact]
    public void Generate_SameSourceTwice_EmitsIdenticalOutput() =>
        GeneratorDriverHarness.AssertDeterministic(new SerializeFormatterGenerator(), StructSource);

    [Fact]
    public void Generate_ClassWithoutStaticCreate_ReportsNalix059()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new SerializeFormatterGenerator(), ClassMissingCreateSource);

        string[] ids = [.. result.GeneratorDiagnostics.Select(static d => d.Id)];
        Assert.Contains("NALIX059", ids);
        Assert.Empty(result.GeneratedSources);
    }
}
