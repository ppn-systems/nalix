// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class SerializeHeaderAnalyzerTests
{
    [Fact]
    public async Task Analyze_HeaderAndOrderOnSameMember_ReportsNalix034()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class ExplicitPacket
{
    [SerializeHeader(1)]
    [SerializeOrder(1)]
    public int A { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX034");
    }

    [Fact]
    public async Task Analyze_SerializeOrderZeroOnPacketBase_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class MyPacket : PacketBase<MyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    [SerializeOrder(0)]
    public int FieldA { get; set; }

    [SerializeOrder(1)]
    public int FieldB { get; set; }

    public static new MyPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<MyPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_SerializeOrderBelow12OnPacketBase_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class MyPacket : PacketBase<MyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    [SerializeOrder(5)]
    public int Payload { get; set; }

    [SerializeOrder(11)]
    public int AnotherPayload { get; set; }

    public static new MyPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<MyPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_SerializeHeaderZeroOnPacketBase_ReportsNalix022()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class MyPacket : PacketBase<MyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    [SerializeHeader(0)]
    public int Reserved { get; set; }

    public static new MyPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<MyPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX022");
    }

    [Fact]
    public async Task Analyze_SerializeHeaderNonZeroOnPacketBase_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class MyPacket : PacketBase<MyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    [SerializeHeader(1)]
    public int MyHeader { get; set; }

    public static new MyPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<MyPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_SerializeHeaderZeroOnNonPacketBase_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class PlainType
{
    [SerializeHeader(0)]
    public int Field { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }
}
