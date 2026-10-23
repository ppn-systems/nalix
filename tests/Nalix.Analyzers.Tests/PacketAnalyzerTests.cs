// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Analyzers.CodeFixes;
using Xunit;

namespace Nalix.Analyzers.Tests;

public sealed class PacketAnalyzerTests
{
    [Fact]
    public async Task PacketDeserializeSpanOverloadMissing_ProducesDiagnostic()
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
    public async Task PacketBaseWithInheritedSpanDeserialize_DoesNotProduceDiagnostic()
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
    public async Task NonPacketDeserializeUtility_DoesNotProducePacketDiagnostic()
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
    public async Task ResetForPoolWithoutBaseCall_ProducesDiagnosticAndFix()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class MyPacket : PacketBase<MyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public override void ResetForPool()
    {
    }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class MyPacket : PacketBase<MyPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public override void ResetForPool()
    {
        base.ResetForPool();
    }
}
""";

        await Verifier<ResetForPoolCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX020",
            actionIndex: 0,
            expectedTitle: "Add base.ResetForPool()",
            expectedEquivalenceKey: "Nalix.Packet.ResetForPool.AddBaseCall");
    }

    [Fact]
    public async Task PacketBaseSelfTypeMismatch_ProducesDiagnosticAndFix()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class WrongPacket : PacketBase<OtherPacket>
{
}

public sealed class OtherPacket : PacketBase<OtherPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class WrongPacket : PacketBase<WrongPacket>
{
}

public sealed class OtherPacket : PacketBase<OtherPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

}
""";

        await Verifier<PacketSelfTypeCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX010",
            actionIndex: 0,
            expectedTitle: "Fix PacketBase<TSelf> to use containing type",
            expectedEquivalenceKey: "Nalix.PacketBase.SelfType.Fix");
    }

    [Fact]
    public async Task PacketDeserializerSelfTypeMismatch_ProducesDiagnosticAndFix()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

public sealed class WrongPacket : PacketBase<WrongPacket>, IPacketDeserializer<OtherPacket>
{
    public static new WrongPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<WrongPacket>.Deserialize(buffer);
}

public sealed class OtherPacket : PacketBase<OtherPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new OtherPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<OtherPacket>.Deserialize(buffer);
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

public sealed class WrongPacket : PacketBase<WrongPacket>, IPacketDeserializer<WrongPacket>
{
    public static new WrongPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<WrongPacket>.Deserialize(buffer);
}

public sealed class OtherPacket : PacketBase<OtherPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new OtherPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<OtherPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketSelfTypeCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX011",
            actionIndex: 0,
            expectedTitle: "Fix IPacketDeserializer<T> to use containing type",
            expectedEquivalenceKey: "Nalix.PacketDeserializer.SelfType.Fix");
    }

    [Fact]
    public async Task DuplicatePacketStaticOpCode_ProducesDiagnostic()
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
    public async Task ManualResetForPool_WhenMutablePropertyNotReset_ProducesDiagnostic()
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
    public async Task ManualResetForPool_WhenAllMutableMembersResetOrSkipClean_DoesNotProduceDiagnostic()
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
















