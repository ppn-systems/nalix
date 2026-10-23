// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Nalix.Environment.Memory;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class NetworkApplicationBuilderAnalyzerTests
{
    [Fact]
    public async Task Analyze_BuilderWithoutRecommendedSetup_ReportsNalix040Nalix041Nalix044()
    {
        const string source = """
namespace Demo;
using Nalix.Hosting;

public static class Setup
{
    public static void Configure()
    {
        _ = new NetworkApplicationBuilder().Build();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX040", "NALIX041", "NALIX044");
    }

    [Fact]
    public async Task Analyze_BuilderConfiguredThroughLocalVariable_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Framework.Memory.Buffers;
using Nalix.Network.Connections;

public static class Setup
{
    public static void Configure()
    {
        var builder = new NetworkApplicationBuilder();
        builder.UseBufferPoolManager(new BufferPoolManager());
        builder.UseConnectionHub(new ConnectionHub());
        builder.ListenTcp<DefaultProtocol>();
        _ = builder.Build();
    }
}
""";

        // Local variables are now tracked, so this should NOT report diagnostics.
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_BuilderConfiguredThroughFluentChain_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Framework.Memory.Buffers;
using Nalix.Network.Connections;

public static class Setup
{
    public static void Configure()
    {
        _ = new NetworkApplicationBuilder()
            .UseBufferPoolManager(new BufferPoolManager())
            .UseConnectionHub(new ConnectionHub())
            .ListenTcp<DefaultProtocol>()
            .Bind()
            .Build();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_BuilderWithUdpWithoutTcp_ReportsNalix045()
    {
        const string source = """
namespace Demo;
using Nalix.Hosting;

public class DummyProtocol { }

public static class Setup
{
    public static void Configure()
    {
        _ = new NetworkApplicationBuilder()
            .ListenUdp<DummyProtocol>().OnPort(9000).Bind()
            .Build();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX040", "NALIX041", "NALIX044", "NALIX045");
    }

    [Fact]
    public async Task Analyze_MapHandlersWithInvalidType_ReportsNalix042()
    {
        const string source = """
namespace Demo;
using Nalix.Hosting;

public interface IHandler { }

public static class Setup
{
    public static void Configure()
    {
        _ = new NetworkApplicationBuilder().MapHandlers<IHandler>();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX042");
    }

    [Fact]
    public async Task Analyze_AddMetadataProviderWithInvalidType_ReportsNalix043()
    {
        const string source = """
namespace Demo;
using Nalix.Hosting;

public interface IProvider { }

public static class Setup
{
    public static void Configure()
    {
        _ = new NetworkApplicationBuilder().AddMetadataProvider<IProvider>();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX043");
    }
}
