// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class PacketContextEscapeAnalyzerTests
{
    [Fact]
    public async Task Analyze_ContextAssignedToField_ReportsNalix076()
    {
        const string source = """
namespace Nalix.Runtime.Handlers;

using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;

public sealed class BadHandler
{
    private PacketContext<DemoPacket>? _saved;

    public void Handle(PacketContext<DemoPacket> context)
    {
        _saved = context;
    }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime", "NALIX076");
    }

    [Fact]
    public async Task Analyze_ContextPacketAssignedToField_ReportsNalix076()
    {
        const string source = """
namespace Nalix.Runtime.Handlers;

using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;

public sealed class BadHandler
{
    private DemoPacket? _savedPacket;

    public void Handle(PacketContext<DemoPacket> context)
    {
        _savedPacket = context.Packet;
    }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime", "NALIX076");
    }

    [Fact]
    public async Task Analyze_TaskRunWithContext_ReportsNalix076()
    {
        const string source = """
namespace Nalix.Runtime.Handlers;

using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;

public sealed class BadHandler
{
    public void Handle(PacketContext<DemoPacket> context)
    {
        _ = System.Threading.Tasks.Task.Run(() => ProcessLater(context));
    }

    private static void ProcessLater(PacketContext<DemoPacket> ctx) { }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime", "NALIX076");
    }

    [Fact]
    public async Task Analyze_TaskRunLambdaCapturingContext_ReportsNalix076()
    {
        const string source = """
namespace Nalix.Runtime.Handlers;

using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;

public sealed class BadHandler
{
    public void Handle(PacketContext<DemoPacket> context)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(100);
            var p = context.Packet;
        });
    }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime", "NALIX076");
    }

    [Fact]
    public async Task Analyze_ContextPassedToHelper_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Runtime.Handlers;

using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;

public sealed class GoodHandler
{
    public async ValueTask Handle(PacketContext<DemoPacket> context)
    {
        await ProcessAsync(context);
    }

    private static async ValueTask ProcessAsync(PacketContext<DemoPacket> ctx)
    {
        await System.Threading.Tasks.Task.CompletedTask;
    }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime");
    }

    [Fact]
    public async Task Analyze_PacketExtractedToLocal_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Runtime.Handlers;

using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;

public sealed class GoodHandler
{
    public void Handle(PacketContext<DemoPacket> context)
    {
        DemoPacket packet = context.Packet;
        var name = packet.GetType().Name;
    }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 1;
    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Runtime");
    }

    [Fact]
    public async Task Analyze_ContextEscapeInNonCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace MyApp.Handlers;

using Nalix.Abstractions.Networking.Packets;

public sealed class BadHandler
{
    private object? _saved;

    public void Handle(IPacketContext<MyPacket> context)
    {
        _saved = context;
    }
}

public sealed class MyPacket : IPacket { }
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "MyApp");
    }
}
