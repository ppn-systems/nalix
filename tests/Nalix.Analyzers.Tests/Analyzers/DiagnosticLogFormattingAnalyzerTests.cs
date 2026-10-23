// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class DiagnosticLogFormattingAnalyzerTests
{
    [Fact]
    public async Task Analyze_InterpolationInDiagnosticLog_ReportsNalix074()
    {
        const string source = """
namespace Nalix.Network.Internal;

using Nalix.Abstractions.Diagnostics;

public static class LogHelper
{
    public static void Log(string endpoint, int count)
    {
        DiagnosticsEvents.Write("test", new DiagnosticLog("tag", $"endpoint={endpoint} count={count}"));
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network", "NALIX074");
    }

    [Fact]
    public async Task Analyze_StringFormatInDiagnosticLog_ReportsNalix074()
    {
        const string source = """
namespace Nalix.Network.Internal;

using Nalix.Abstractions.Diagnostics;

public static class LogHelper
{
    public static void Log(string endpoint, int count)
    {
        DiagnosticsEvents.Write("test", new DiagnosticLog("tag", string.Format("endpoint={0} count={1}", endpoint, count)));
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network", "NALIX074");
    }

    [Fact]
    public async Task Analyze_StringConcatInDiagnosticLog_ReportsNalix074()
    {
        const string source = """
namespace Nalix.Network.Internal;

using Nalix.Abstractions.Diagnostics;

public static class LogHelper
{
    public static void Log(string endpoint)
    {
        DiagnosticsEvents.Write("test", new DiagnosticLog("tag", "remote=" + endpoint));
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network", "NALIX074");
    }

    [Fact]
    public async Task Analyze_ConstantStringInDiagnosticLog_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Network.Internal;

using Nalix.Abstractions.Diagnostics;

public static class LogHelper
{
    public static void Log()
    {
        DiagnosticsEvents.Write("test", new DiagnosticLog("tag", "connection-closed"));
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network");
    }

    [Fact]
    public async Task Analyze_InterpolationOutsideDiagnosticLog_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Network.Internal;

public static class LogHelper
{
    public static string Format(string endpoint, int count)
    {
        return $"endpoint={endpoint} count={count}";
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network");
    }

    [Fact]
    public async Task Analyze_DiagnosticLogInNonCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace MyApp.Logging;

using Nalix.Abstractions.Diagnostics;

public static class LogHelper
{
    public static void Log(string endpoint)
    {
        var log = new DiagnosticLog("tag", $"endpoint={endpoint}");
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "MyApp");
    }

    [Fact]
    public async Task Analyze_DiagnosticLogInTestAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Network.Tests;

using Nalix.Abstractions.Diagnostics;

public static class LogHelper
{
    public static void Log(string endpoint)
    {
        var log = new DiagnosticLog("tag", $"endpoint={endpoint}");
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Tests");
    }

    [Fact]
    public async Task Analyze_NameofInDiagnosticLog_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Network.Internal;

using Nalix.Abstractions.Diagnostics;

public static class LogHelper
{
    public static void Log()
    {
        DiagnosticsEvents.Write("test", new DiagnosticLog(nameof(LogHelper), "initialized"));
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network");
    }

    [Fact]
    public async Task Analyze_InterpolationInsideIsEnabledGuard_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Network.Internal;

using Nalix.Abstractions.Diagnostics;

public static class LogHelper
{
    public static void Log(string endpoint, int count)
    {
        if (DiagnosticsEvents.Source.IsEnabled("test"))
        {
            DiagnosticsEvents.Write("test", new DiagnosticLog("tag", $"endpoint={endpoint} count={count}"));
        }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network");
    }
}
