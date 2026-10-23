// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nalix.Analyzers.Tests.TestSupport;

/// <summary>
/// Layer 1 test harness: compiles a source snippet against the real Nalix assemblies and
/// runs a real <see cref="IIncrementalGenerator"/> through <see cref="CSharpGeneratorDriver"/>,
/// exposing the resulting diagnostics, generated trees, and post-generation compilation.
/// </summary>
/// <remarks>
/// Unlike <see cref="Verifier{TCodeFix}"/> (which uses a fake stub API surface for analyzer
/// diagnostic testing), this harness references the real assemblies so generator output can be
/// checked for genuine compile-cleanliness, not just against a stand-in prelude.
/// </remarks>
internal static class GeneratorDriverHarness
{
    /// <summary>
    /// Result of running a generator once: the generator diagnostics, the resulting compilation
    /// (source + generated trees), and the generated source texts keyed by hint name.
    /// </summary>
    public readonly struct Result(
        ImmutableArray<Diagnostic> generatorDiagnostics,
        CSharpCompilation outputCompilation,
        ImmutableArray<(string HintName, string Text)> generatedSources)
    {
        public ImmutableArray<Diagnostic> GeneratorDiagnostics { get; } = generatorDiagnostics;
        public CSharpCompilation OutputCompilation { get; } = outputCompilation;
        public ImmutableArray<(string HintName, string Text)> GeneratedSources { get; } = generatedSources;
    }

    /// <summary>
    /// Runs <paramref name="generator"/> against <paramref name="source"/> compiled with the real
    /// Nalix assemblies on the trusted platform path.
    /// </summary>
    public static Result Run(IIncrementalGenerator generator, string source, string assemblyName = "GeneratorHarnessAssembly")
        => Run([generator], source, assemblyName);

    /// <summary>
    /// Runs multiple generators together (e.g. when one generator's output depends on another's,
    /// such as <c>PacketSchemaGenerator</c> providing <c>Length</c>/<c>ResetForPool</c> overrides
    /// that <c>PacketRegistryGenerator</c>'s output compiles against).
    /// </summary>
    public static Result Run(IIncrementalGenerator[] generators, string source, string assemblyName = "GeneratorHarnessAssembly")
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));

        string[] trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

        MetadataReference[] references = [.. trustedAssemblies
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path))];

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [.. generators.Select(static g => g.AsSourceGenerator())],
            parseOptions: (CSharpParseOptions)tree.Options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation outputCompilation, out ImmutableArray<Diagnostic> diagnostics);

        GeneratorDriverRunResult runResult = driver.GetRunResult();
        ImmutableArray<(string HintName, string Text)> generatedSources =
            [.. runResult.Results.SelectMany(static r => r.GeneratedSources)
                .Select(static s => (s.HintName, s.SourceText.ToString()))];

        return new Result(diagnostics, (CSharpCompilation)outputCompilation, generatedSources);
    }

    /// <summary>
    /// Runs the generator twice against identical input and asserts the generated source set is
    /// byte-identical both times (determinism check).
    /// </summary>
    public static void AssertDeterministic(IIncrementalGenerator generator, string source)
        => AssertDeterministic([generator], source);

    /// <summary>
    /// Multi-generator overload of <see cref="AssertDeterministic(IIncrementalGenerator, string)"/>.
    /// </summary>
    public static void AssertDeterministic(IIncrementalGenerator[] generators, string source)
    {
        Result first = Run(generators, source);
        Result second = Run(generators, source);

        string[] firstTexts = [.. first.GeneratedSources.OrderBy(static s => s.HintName).Select(static s => $"{s.HintName}\n{s.Text}")];
        string[] secondTexts = [.. second.GeneratedSources.OrderBy(static s => s.HintName).Select(static s => $"{s.HintName}\n{s.Text}")];

        Xunit.Assert.Equal(firstTexts, secondTexts);
    }

    /// <summary>
    /// Asserts the output compilation (source + generated code) has zero <see cref="DiagnosticSeverity.Error"/>
    /// diagnostics.
    /// </summary>
    public static void AssertNoCompileErrors(Result result)
    {
        Diagnostic[] errors = [.. result.OutputCompilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];
        Xunit.Assert.True(errors.Length == 0, string.Join(System.Environment.NewLine, errors.Select(static d => d.ToString())));
    }
}
