// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class PacketScopeAnalyzerTests
{
    [Fact]
    public async Task Analyze_PacketScopeWithoutUsing_ReportsNalix075()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;

public static class PoolDemo
{
    public static void Use()
    {
        PacketScope<DummyPacket> scope = new PacketScope<DummyPacket>(new DummyPacket());
        _ = scope.Value;
    }
}

public sealed class DummyPacket : PacketBase<DummyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DummyPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DummyPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX075");
    }

    [Fact]
    public async Task Analyze_PacketScopeWithUsingDeclaration_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;

public static class PoolDemo
{
    public static void Use()
    {
        using PacketScope<DummyPacket> scope = new PacketScope<DummyPacket>(new DummyPacket());
        _ = scope.Value;
    }
}

public sealed class DummyPacket : PacketBase<DummyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DummyPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DummyPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_PacketScopeWithUsingStatement_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;

public static class PoolDemo
{
    public static void Use()
    {
        using (PacketScope<DummyPacket> scope = new PacketScope<DummyPacket>(new DummyPacket()))
        {
            _ = scope.Value;
        }
    }
}

public sealed class DummyPacket : PacketBase<DummyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DummyPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DummyPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_UnrelatedDisposableWithoutUsing_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;

public sealed class MyDisposable : System.IDisposable
{
    public void Dispose() { }
}

public static class DemoClass
{
    public static void Use()
    {
        MyDisposable d = new MyDisposable();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_PacketScopeVarWithoutUsing_ReportsNalix075()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;

public static class PoolDemo
{
    public static void Use()
    {
        var scope = new PacketScope<DummyPacket>(new DummyPacket());
        _ = scope.Value;
    }
}

public sealed class DummyPacket : PacketBase<DummyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DummyPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DummyPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX075");
    }
}
