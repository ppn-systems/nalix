// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class DispatchLoopCountAnalyzerTests
{
    [Fact]
    public async Task Analyze_LoopCountOutOfRange_ReportsNalix047()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public static class DispatchSetup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<DemoPacket>().WithDispatchLoopCount(0);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX047");
    }

    [Fact]
    public async Task Analyze_LoopCountAtBoundaries_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public static class DispatchSetup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<DemoPacket>()
            .WithDispatchLoopCount(1)
            .WithDispatchLoopCount(64);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }
}
