// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class ResetForPoolCodeFixTests
{
    [Fact]
    public async Task Fix_ResetForPoolMissingBaseCall_InsertsBaseInvocation()
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

        await AnalyzerTestHarness.AssertCodeFixAsync<ResetForPoolCodeFixProvider>(
            source,
            fixedSource,
            diagnosticId: "NALIX020");
    }

    [Fact]
    public async Task Fix_ResetForPoolWithoutBaseCall_AddsBaseInvocation()
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
}
