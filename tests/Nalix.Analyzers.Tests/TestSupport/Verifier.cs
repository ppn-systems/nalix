// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
using Nalix.Abstractions.Serialization;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Nalix.Analyzers.Analyzers;
using Xunit;

namespace Nalix.Analyzers.Tests.TestSupport;

internal static class Verifier<TCodeFix>
    where TCodeFix : CodeFixProvider, new()
{
    public static async Task VerifyAnalyzerAsync(string source, params string[] expectedDiagnosticIds)
        => await VerifyAnalyzerAsync([source], expectedDiagnosticIds).ConfigureAwait(false);

    public static async Task VerifyAnalyzerInAssemblyAsync(string source, string assemblyName, params string[] expectedDiagnosticIds)
        => await VerifyAnalyzerInAssemblyAsync([source], assemblyName, expectedDiagnosticIds).ConfigureAwait(false);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1310:Specify StringComparison for correctness", Justification = "<Pending>")]
    public static async Task VerifyAnalyzerAsync(string[] sources, params string[] expectedDiagnosticIds)
    {
        Project project = CreateProject(sources);
        Document document = project.Documents.First(d => d.Name != "Prelude.cs");
        ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsAsync(document).ConfigureAwait(false);

        // Filter diagnostics to only those in TestX.cs files
        string[] actual = [.. diagnostics
            .Where(d => d.Location.SourceTree?.FilePath?.StartsWith("Test") == true)
            .Select(d => d.Id)
            .OrderBy(x => x)];

        string[] expected = [.. expectedDiagnosticIds.OrderBy(x => x)];
        Assert.Equal(expected, actual);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1310:Specify StringComparison for correctness", Justification = "<Pending>")]
    public static async Task VerifyAnalyzerInAssemblyAsync(string[] sources, string assemblyName, params string[] expectedDiagnosticIds)
    {
        Project project = CreateProject(sources, assemblyName);
        Document document = project.Documents.First(d => d.Name != "Prelude.cs");
        ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsAsync(document).ConfigureAwait(false);

        string[] actual = [.. diagnostics
            .Where(d => d.Location.SourceTree?.FilePath?.StartsWith("Test") == true)
            .Select(d => d.Id)
            .OrderBy(x => x)];

        string[] expected = [.. expectedDiagnosticIds.OrderBy(x => x)];
        Assert.Equal(expected, actual);
    }

    public static async Task VerifyCodeFixAsync(string source, string fixedSource, string diagnosticId)
        => await VerifyCodeFixAsync(source, fixedSource, diagnosticId, 0).ConfigureAwait(false);

    public static async Task VerifyCodeFixAsync(string source, string fixedSource, string diagnosticId, int actionIndex)
        => await VerifyCodeFixAsync(source, fixedSource, diagnosticId, actionIndex, expectedTitle: null, expectedEquivalenceKey: null).ConfigureAwait(false);

    public static async Task VerifyCodeFixAsync(
        string source,
        string fixedSource,
        string diagnosticId,
        int actionIndex,
        string? expectedTitle,
        string? expectedEquivalenceKey)
    {
        Document document = CreateDocument(source);
        ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsAsync(document).ConfigureAwait(false);
        Diagnostic diagnostic = diagnostics.First(d => d.Id == diagnosticId);
        await VerifyCodeFixCoreAsync(document, diagnostic, fixedSource, actionIndex, expectedTitle, expectedEquivalenceKey).ConfigureAwait(false);
    }

    public static async Task VerifyCodeFixWithSyntheticDiagnosticAsync(
        string source,
        string fixedSource,
        string diagnosticId,
        string diagnosticMessage,
        string locateText,
        int actionIndex = 0)
        => await VerifyCodeFixWithSyntheticDiagnosticAsync(
            source,
            fixedSource,
            diagnosticId,
            diagnosticMessage,
            locateText,
            actionIndex,
            expectedTitle: null,
            expectedEquivalenceKey: null).ConfigureAwait(false);

    public static async Task VerifyCodeFixWithSyntheticDiagnosticAsync(
        string source,
        string fixedSource,
        string diagnosticId,
        string diagnosticMessage,
        string locateText,
        int actionIndex,
        string? expectedTitle,
        string? expectedEquivalenceKey)
    {
        Document document = CreateDocument(source);
        int start = source.IndexOf(locateText, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find text '{locateText}' in source.");
        SyntaxTree syntaxTree = (await document.GetSyntaxTreeAsync().ConfigureAwait(false))!;
        FileLinePositionSpan lineSpan = syntaxTree.GetLineSpan(new TextSpan(start, locateText.Length));

        DiagnosticDescriptor descriptor = new(
            diagnosticId,
            diagnosticId,
            "{0}",
            "Test",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        Diagnostic diagnostic = Diagnostic.Create(
            descriptor,
            Location.Create(
                document.FilePath ?? "Test.cs",
                new TextSpan(start, locateText.Length),
                lineSpan.Span),
            diagnosticMessage);
        await VerifyCodeFixCoreAsync(document, diagnostic, fixedSource, actionIndex, expectedTitle, expectedEquivalenceKey).ConfigureAwait(false);
    }

    private static async Task VerifyCodeFixCoreAsync(
        Document document,
        Diagnostic diagnostic,
        string fixedSource,
        int actionIndex,
        string? expectedTitle,
        string? expectedEquivalenceKey)
    {
        CodeFixProvider codeFix = new TCodeFix();
        List<CodeAction> actions = [];
        CodeFixContext context = new(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFix.RegisterCodeFixesAsync(context).ConfigureAwait(false);
        Assert.True(actions.Count > actionIndex, $"Expected at least {actionIndex + 1} code fix action(s), got {actions.Count}.");

        CodeAction action = actions[actionIndex];
        if (expectedTitle is not null)
        {
            Assert.Equal(expectedTitle, action.Title);
        }

        if (expectedEquivalenceKey is not null)
        {
            Assert.Equal(expectedEquivalenceKey, action.EquivalenceKey);
        }

        ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
        ApplyChangesOperation applyChanges = operations.OfType<ApplyChangesOperation>().Single();
        Document updatedDocument = applyChanges.ChangedSolution.GetDocument(document.Id)!;
        string actual = (await updatedDocument.GetTextAsync().ConfigureAwait(false)).ToString();
        Assert.Equal(Normalize(fixedSource), Normalize(actual));
    }

    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(Document document)
    {
        Compilation compilation = await document.Project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Compilation could not be created.");
        Diagnostic[] compilationErrors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(compilationErrors.Length == 0, string.Join(System.Environment.NewLine, compilationErrors.Select(static d => d.ToString())));
        string[] requiredMetadataNames =
        [
            "Nalix.Abstractions.Networking.Packets.PacketOpcodeAttribute",
            "Nalix.Abstractions.Networking.Packets.PacketHandlerAttribute",
            "Nalix.Abstractions.Networking.Packets.IPacket",
            "Nalix.Codec.DataFrames.PacketBase`1",
            "Nalix.Abstractions.Serialization.SerializeHeaderAttribute",
            "Nalix.Abstractions.Serialization.SerializePackableAttribute",
            "Nalix.Abstractions.Serialization.SerializeIgnoreAttribute",
            "Nalix.Abstractions.Serialization.SerializeDynamicSizeAttribute",
            "Nalix.Abstractions.Serialization.SerializeLayout",
            "Nalix.Abstractions.Networking.Packets.PacketHeaderOffset",
            "Nalix.Runtime.Dispatching.PacketContext`1",
            "Nalix.Abstractions.Networking.IConnection",
            "Nalix.Network.Routing.PacketDispatchOptions`1",
            "Nalix.Codec.DataFrames.PacketRegistryFactory",
            "Nalix.Abstractions.Networking.Packets.IPacketDeserializer`1",
            "Nalix.Abstractions.Middleware.IPacketMiddleware`1",
            "Nalix.Abstractions.Serialization.SerializeOrderAttribute",
            "Nalix.Abstractions.Middleware.MiddlewareOrderAttribute",
            "Nalix.Abstractions.Middleware.MiddlewareStageAttribute",
            "Nalix.Abstractions.Middleware.MiddlewareStage",
            "Nalix.Environment.Configuration.Binding.ConfigurationLoader",
            "Nalix.Abstractions.ConfigurationIgnoreAttribute",
            "Nalix.Runtime.Dispatching.IPacketMetadataProvider",
            "Nalix.Runtime.Dispatching.PacketMetadataBuilder",
            "System.Reflection.MethodInfo",
            "Nalix.SDK.Options.RequestOptions",
            "Nalix.SDK.Transport.Extensions.RequestExtensions",
            "Nalix.SDK.Transport.TcpSession",
            "Nalix.Hosting.NetworkApplicationBuilder",
            "Nalix.Abstractions.Serialization.IFixedSizeSerializable",
            "Nalix.Abstractions.Networking.Packets.IPacketContext`1",
            "Nalix.Abstractions.IBufferLease",
            "Nalix.Runtime.Dispatching.IPacketDispatch",
            "Nalix.Abstractions.Exceptions.ExceptionClassifier",
            "Nalix.Codec.Pooling.PacketScope`1"
        ];
        string[] missingMetadata = [.. requiredMetadataNames.Where(name => compilation.GetTypeByMetadataName(name) is null)];
        Assert.True(missingMetadata.Length == 0, "Missing metadata: " + string.Join(", ", missingMetadata));

        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            [new NalixUsageAnalyzer()]);

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
    }

    private static Document CreateDocument(string source) => CreateProject([source]).Documents.First(d => d.Name == "Test0.cs");

    private static Project CreateProject(string[] sources, string? assemblyName = null)
    {
        ProjectId projectId = ProjectId.CreateNewId();
        string[] trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? [];

        string effectiveAssemblyName = assemblyName ?? "TestProject";

        Solution solution = new AdhocWorkspace().CurrentSolution
            .AddProject(projectId, "TestProject", effectiveAssemblyName, LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Preview, documentationMode: DocumentationMode.Parse))
            .AddDocument(DocumentId.CreateNewId(projectId), "Prelude.cs", SourceText.From(TestSources.Prelude));

        for (int i = 0; i < sources.Length; i++)
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), $"Test{i}.cs", SourceText.From(sources[i]));
        }

        foreach (string assemblyPath in trustedAssemblies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(assemblyPath));
        }

        return solution.GetProject(projectId)!;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}

















