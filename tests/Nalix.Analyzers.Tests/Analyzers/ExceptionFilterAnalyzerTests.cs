// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class ExceptionFilterAnalyzerTests
{
    [Fact]
    public async Task Analyze_BareCatchExceptionInCoreAssembly_ReportsNalix073()
    {
        const string source = """
namespace Nalix.Runtime.Internal;

public static class Processor
{
    public static void Process()
    {
        try { }
        catch (System.Exception) { }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime", "NALIX073");
    }

    [Fact]
    public async Task Analyze_GuardedCatchExceptionInCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Runtime.Internal;

public static class Processor
{
    public static void Process()
    {
        try { }
        catch (System.Exception ex) when (Nalix.Abstractions.Exceptions.ExceptionClassifier.IsNonFatal(ex)) { }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime");
    }

    [Fact]
    public async Task Analyze_SpecificExceptionCatchInCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Runtime.Internal;

public static class Processor
{
    public static void Process()
    {
        try { }
        catch (System.IO.IOException) { }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime");
    }

    [Fact]
    public async Task Analyze_BareCatchExceptionInNonCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace MyApp.Services;

public static class Processor
{
    public static void Process()
    {
        try { }
        catch (System.Exception) { }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "MyApp");
    }

    [Fact]
    public async Task Analyze_BareCatchExceptionInConsumerNamespace_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Message.Handlers;

public static class Processor
{
    public static void Process()
    {
        try { }
        catch (System.Exception) { }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Message");
    }

    [Fact]
    public async Task Analyze_BareCatchExceptionInTestAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Runtime.Tests;

public static class Processor
{
    public static void Process()
    {
        try { }
        catch (System.Exception) { }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Tests");
    }

    [Fact]
    public async Task Analyze_CatchExceptionWithUnrelatedFilter_ReportsNalix073()
    {
        const string source = """
namespace Nalix.Core.Processing;

public static class Processor
{
    public static void Process()
    {
        try { }
        catch (System.Exception ex) when (ex is not System.ObjectDisposedException) { }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime", "NALIX073");
    }
}
