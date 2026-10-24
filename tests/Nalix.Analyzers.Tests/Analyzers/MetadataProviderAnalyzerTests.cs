// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class MetadataProviderAnalyzerTests
{
    [Fact]
    public async Task Analyze_PopulateClearingOpcode_ReportsNalix025()
    {
        const string source = """
namespace Demo;
using System.Reflection;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class BadProvider : IPacketMetadataProvider
{
    public void Populate(MethodInfo method, PacketMetadataBuilder builder)
    {
        builder.Opcode = null;
    }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX025");
    }

    [Fact]
    public async Task Analyze_PopulateOverwritingOpcodeUnguarded_ReportsNalix026()
    {
        const string source = """
namespace Demo;
using System.Reflection;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class BadProvider : IPacketMetadataProvider
{
    public void Populate(MethodInfo method, PacketMetadataBuilder builder)
    {
        builder.Opcode = new PacketOpcodeAttribute(7);
    }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX026");
    }

    [Fact]
    public async Task Analyze_PopulateOverwritingOpcodeGuarded_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using System.Reflection;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class GoodProvider : IPacketMetadataProvider
{
    public void Populate(MethodInfo method, PacketMetadataBuilder builder)
    {
        if (builder.Opcode is null)
        {
            builder.Opcode = new PacketOpcodeAttribute(7);
        }
    }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task Analyze_ProviderClearingOpcode_ReportsNalix025()
    {
        const string source = """
namespace Demo;
using System.Reflection;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class Provider : IPacketMetadataProvider
{
    public void Populate(MethodInfo method, PacketMetadataBuilder builder)
    {
        builder.Opcode = null;
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX025");
    }

    [Fact]
    public async Task Analyze_ProviderOverwritingOpcodeWithoutGuard_ReportsNalix026()
    {
        const string source = """
namespace Demo;
using System.Reflection;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class Provider : IPacketMetadataProvider
{
    public void Populate(MethodInfo method, PacketMetadataBuilder builder)
    {
        builder.Opcode = new PacketOpcodeAttribute(0x1210);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX026");
    }

    [Fact]
    public async Task Analyze_ProviderOverwritingOpcodeWithGuard_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using System.Reflection;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class Provider : IPacketMetadataProvider
{
    public void Populate(MethodInfo method, PacketMetadataBuilder builder)
    {
        if (builder.Opcode is null)
        {
            builder.Opcode = new PacketOpcodeAttribute(0x1210);
        }
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }
}
