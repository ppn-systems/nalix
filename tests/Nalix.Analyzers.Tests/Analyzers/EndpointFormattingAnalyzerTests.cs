// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class EndpointFormattingAnalyzerTests
{
    [Fact]
    public async Task Analyze_IPAddressToStringInCoreAssembly_ReportsNalix072()
    {
        const string source = """
namespace Nalix.Network.Internal;

public static class EndpointHelper
{
    public static string Format(System.Net.IPAddress addr)
    {
        return addr.ToString();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network", "NALIX072");
    }

    [Fact]
    public async Task Analyze_NetworkEndpointAddressInCoreAssembly_ReportsNalix072()
    {
        const string source = """
namespace Nalix.Network.Internal;

using Nalix.Abstractions.Networking;

public static class EndpointHelper
{
    public static string GetAddress(INetworkEndpoint ep)
    {
        return ep.Address;
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network", "NALIX072");
    }

    [Fact]
    public async Task Analyze_NetworkEndpointAddressInsideTryFormat_ReportsNalix072()
    {
        const string source = """
namespace Nalix.Network.Internal;

using Nalix.Abstractions.Networking;

public static class EndpointHelper
{
    public static bool FormatEndpoint(INetworkEndpoint ep, System.Span<char> dest)
    {
        return TryFormatAddress(ep, dest);
    }

    private static bool TryFormatAddress(INetworkEndpoint ep, System.Span<char> dest)
    {
        string addr = ep.Address;
        return addr.AsSpan().TryCopyTo(dest);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network", "NALIX072");
    }

    [Fact]
    public async Task Analyze_IPAddressInNonCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace MyApp.Network;

public static class EndpointHelper
{
    public static string Format(System.Net.IPAddress addr)
    {
        return addr.ToString();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "MyApp");
    }

    [Fact]
    public async Task Analyze_IPAddressInTestAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Network.Tests;

public static class EndpointHelper
{
    public static string Format(System.Net.IPAddress addr)
    {
        return addr.ToString();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Tests");
    }

    [Fact]
    public async Task Analyze_UnrelatedToStringInCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Network.Internal;

public static class Helper
{
    public static string Format(int value)
    {
        return value.ToString();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network");
    }

    [Fact]
    public async Task Analyze_IPAddressInterpolationInCoreAssembly_ReportsNalix072()
    {
        const string source = """
namespace Nalix.Network.Internal;

public static class EndpointHelper
{
    public static string Format(System.Net.IPAddress addr)
    {
        return $"address={addr}";
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Network");
    }
}
