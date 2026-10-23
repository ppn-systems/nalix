// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class PacketRegistrationAnalyzerTests
{
    [Fact]
    public async Task Analyze_AbstractPacketBaseRegistration_ReportsNalix018()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;

public abstract class AbstractPacket : PacketBase<AbstractPacket>
{
}

public sealed class Example
{
    public void Run()
    {
        new PacketRegistryFactory().RegisterPacket<AbstractPacket>();
    }
}
""";

        await Verifier<PacketRegistryDeserializerCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX018");
    }

    [Fact]
    public async Task Analyze_AbstractIPacketRegistration_ReportsNalix018()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

public abstract class AbstractPacket : IPacket { }

public static class Setup
{
    public static void Configure()
    {
        _ = new PacketRegistryFactory().RegisterPacket<AbstractPacket>();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX018");
    }

    [Fact]
    public async Task Analyze_PacketWithoutDeserializeRegistration_ReportsNalix009()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

public sealed class PlainPacket : IPacket { }

public static class Setup
{
    public static void Configure()
    {
        _ = new PacketRegistryFactory().RegisterPacket<PlainPacket>();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX009");
    }
}
