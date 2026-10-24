// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
using Microsoft.CodeAnalysis.CodeFixes;
using Nalix.Analyzers.CodeFixes;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.TestSupport;

internal static class AnalyzerTestHarness
{
    public static Task AssertDiagnosticIdsAsync(string source, params string[] expectedIds)
        => Verifier<ResetForPoolCodeFixProvider>.VerifyAnalyzerAsync(source, expectedIds);

    public static Task AssertDiagnosticIdsInAssemblyAsync(string source, string assemblyName, params string[] expectedIds)
        => Verifier<ResetForPoolCodeFixProvider>.VerifyAnalyzerInAssemblyAsync(source, assemblyName, expectedIds);

    public static Task AssertCodeFixAsync<TCodeFix>(
        string source,
        string expectedFixedSource,
        string diagnosticId,
        int actionIndex = 0)
        where TCodeFix : CodeFixProvider, new()
        => Verifier<TCodeFix>.VerifyCodeFixAsync(source, expectedFixedSource, diagnosticId, actionIndex);
}















