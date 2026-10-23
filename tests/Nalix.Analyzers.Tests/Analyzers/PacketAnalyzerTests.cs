// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Nalix.Environment.Memory;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class PacketAnalyzerTests
{
    [Fact]
    public async Task Analyze_DeserializeWithoutReadOnlySpanOverload_ReportsNalix052()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

public sealed class MissingSpanPacket : IPacket
{
    public static MissingSpanPacket Deserialize(byte[] buffer) => new();
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX052");
    }

    [Fact]
    public async Task Analyze_UtilityDeserializeMethods_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;

public static class DeserializeUtility
{
    public static int Deserialize(byte[] buffer, out int bytesRead)
    {
        bytesRead = buffer.Length;
        return bytesRead;
    }

    public static int Deserialize(ReadOnlyMemory<byte> buffer, out int bytesRead)
    {
        bytesRead = buffer.Length;
        return bytesRead;
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_PacketBaseWithoutExplicitDeserialize_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class MissingDeserializePacket : PacketBase<MissingDeserializePacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_DeserializeWithInvalidSignature_ReportsNalix017()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class BadDeserializePacket : PacketBase<BadDeserializePacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static int Deserialize(ReadOnlySpan<byte> buffer) => 0;
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX017");
    }

    [Fact]
    public async Task Analyze_PacketWithoutSpanDeserializeOverload_ReportsNalix052()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

public sealed class MissingSpanPacket : IPacket
{
    public static MissingSpanPacket Deserialize(byte[] buffer) => new();
}
""";

        await Verifier<ResetForPoolCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX052");
    }

    [Fact]
    public async Task Analyze_PacketBaseWithInheritedSpanDeserialize_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class InheritedSpanPacket : PacketBase<InheritedSpanPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static InheritedSpanPacket Deserialize(byte[] buffer) => new();
}
""";

        await Verifier<ResetForPoolCodeFixProvider>.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task Analyze_NonPacketDeserializeUtility_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;

public static class LiteSerializerLike
{
    public static int Deserialize(byte[] buffer, out int bytesRead)
    {
        bytesRead = buffer.Length;
        return bytesRead;
    }

    public static int Deserialize(ReadOnlyMemory<byte> buffer, out int bytesRead)
    {
        bytesRead = buffer.Length;
        return bytesRead;
    }
}
""";

        await Verifier<ResetForPoolCodeFixProvider>.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task Analyze_DuplicatePacketStaticOpCode_ReportsNalix106()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

[Packet]
public sealed class FirstPacket : PacketBase<FirstPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 4200;
}

[Packet]
public sealed class SecondPacket : PacketBase<SecondPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 4200;
}
""";

        await Verifier<ResetForPoolCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX106");
    }

    [Fact]
    public async Task Analyze_ManualResetForPoolSkippingMutableProperty_ReportsNalix107()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class LeakyPacket : PacketBase<LeakyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public int ResetField { get; set; }
    public string? ForgottenSecret { get; set; }

    public override void ResetForPool()
    {
        base.ResetForPool();
        ResetField = 0;
    }
}
""";

        await Verifier<ResetForPoolCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX107");
    }

    [Fact]
    public async Task Analyze_ManualResetForPoolCoveringAllMembers_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Pooling;
using Nalix.Codec.DataFrames;

public sealed class CleanPacket : PacketBase<CleanPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public int ResetField { get; set; }

    [SkipClean]
    public int KeptField { get; set; }

    public override void ResetForPool()
    {
        base.ResetForPool();
        ResetField = 0;
    }
}
""";

        await Verifier<ResetForPoolCodeFixProvider>.VerifyAnalyzerAsync(source);
    }
}
