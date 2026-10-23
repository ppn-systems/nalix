// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class MiddlewareAnalyzerTests
{
    [Fact]
    public async Task Analyze_PacketMiddlewareWithoutOrder_ReportsNalix030()
    {
        const string source = """
namespace Demo;
using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Middleware;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

public sealed class DemoMiddleware : IPacketMiddleware<LoginPacket>
{
    public ValueTask InvokeAsync(IPacketContext<LoginPacket> context, Func<CancellationToken, ValueTask> next) => next(CancellationToken.None);
}
""";

        await Verifier<MiddlewareCodeFixProvider>.VerifyAnalyzerAsync(source, "NALIX030");
    }

    [Fact]
    public async Task Analyze_InboundMiddlewareWithAlwaysExecute_ReportsNalix032()
    {
        const string source = """
namespace Demo;
using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Middleware;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

[MiddlewareOrder(0)]
[MiddlewareStage(MiddlewareStage.Inbound, AlwaysExecute = true)]
public sealed class DemoMiddleware : IPacketMiddleware<LoginPacket>
{
    public ValueTask InvokeAsync(IPacketContext<LoginPacket> context, Func<CancellationToken, ValueTask> next) => next(CancellationToken.None);
}
""";

        await Verifier<MiddlewareCodeFixProvider>.VerifyAnalyzerAsync(source, "NALIX032");
    }

    [Fact]
    public async Task Analyze_PacketMiddlewareWithOrder_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Middleware;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

[MiddlewareOrder(10)]
public sealed class DemoMiddleware : IPacketMiddleware<LoginPacket>
{
    public ValueTask InvokeAsync(IPacketContext<LoginPacket> context, Func<CancellationToken, ValueTask> next) => next(CancellationToken.None);
}
""";

        await Verifier<MiddlewareCodeFixProvider>.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task Analyze_DuplicateOrderInSingleChain_ReportsNalix033()
    {
        const string source = """
namespace Demo;
using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Middleware;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

[MiddlewareOrder(10)]
public sealed class FirstMiddleware : IPacketMiddleware<LoginPacket>
{
    public ValueTask InvokeAsync(IPacketContext<LoginPacket> context, Func<CancellationToken, ValueTask> next) => next(CancellationToken.None);
}

[MiddlewareOrder(10)]
public sealed class SecondMiddleware : IPacketMiddleware<LoginPacket>
{
    public ValueTask InvokeAsync(IPacketContext<LoginPacket> context, Func<CancellationToken, ValueTask> next) => next(CancellationToken.None);
}

public sealed class Example
{
    public void Run()
    {
        _ = new PacketDispatchOptions<LoginPacket>()
            .WithMiddleware(new FirstMiddleware())
            .WithMiddleware(new SecondMiddleware());
    }
}
""";

        await Verifier<MiddlewareCodeFixProvider>.VerifyAnalyzerAsync(source, "NALIX033");
    }

    [Fact]
    public async Task Analyze_MiddlewareRegistrationWithNullLiteral_ReportsNalix056()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;
using Nalix.Abstractions.Middleware;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public static class DispatchSetup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<DemoPacket>().WithMiddleware(null!);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX056");
    }

    [Fact]
    public async Task Analyze_MiddlewareClassWithoutOrder_ReportsNalix030()
    {
        const string source = """
namespace Demo;
using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Abstractions.Middleware;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public sealed class DemoPacketMiddleware : IPacketMiddleware<DemoPacket>
{
    public ValueTask InvokeAsync(IPacketContext<DemoPacket> context, Func<CancellationToken, ValueTask> next)
        => next(context.CancellationToken);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX030");
    }

    [Fact]
    public async Task Analyze_MiddlewarePacketTypeMismatch_ReportsNalix006()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Middleware;

public sealed class PacketA : PacketBase<PacketA>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new PacketA Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<PacketA>.Deserialize(buffer);
}

public sealed class PacketB : PacketBase<PacketB>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new PacketB Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<PacketB>.Deserialize(buffer);
}

[MiddlewareOrder(1)]
public sealed class PacketBMiddleware : IPacketMiddleware<PacketB>
{
    public ValueTask InvokeAsync(IPacketContext<PacketB> context, Func<CancellationToken, ValueTask> next) => next(context.CancellationToken);
}

public static class Setup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<PacketA>()
            .WithMiddleware((IPacketMiddleware<PacketA>)(object)new PacketBMiddleware());
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX006");
    }

    [Fact]
    public async Task Analyze_InboundMiddlewareAlwaysExecute_ReportsNalix032()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Abstractions.Middleware;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

[MiddlewareOrder(1)]
[MiddlewareStage(MiddlewareStage.Inbound, AlwaysExecute = true)]
public sealed class DemoMiddleware : IPacketMiddleware<DemoPacket>
{
    public ValueTask InvokeAsync(IPacketContext<DemoPacket> context, Func<CancellationToken, ValueTask> next) => next(context.CancellationToken);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX032");
    }

    [Fact]
    public async Task Analyze_TwoMiddlewareWithSameOrderInChain_ReportsNalix033()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;
using Nalix.Abstractions.Middleware;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

[MiddlewareOrder(5)]
public sealed class Mw1 : IPacketMiddleware<DemoPacket>
{
    public ValueTask InvokeAsync(IPacketContext<DemoPacket> context, Func<CancellationToken, ValueTask> next) => next(context.CancellationToken);
}

[MiddlewareOrder(5)]
public sealed class Mw2 : IPacketMiddleware<DemoPacket>
{
    public ValueTask InvokeAsync(IPacketContext<DemoPacket> context, Func<CancellationToken, ValueTask> next) => next(context.CancellationToken);
}

public static class Setup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<DemoPacket>()
            .WithMiddleware(new Mw1())
            .WithMiddleware(new Mw2());
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX033");
    }

    [Fact]
    public async Task Analyze_SameOrderAcrossDifferentChains_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;
using Nalix.Abstractions.Middleware;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

[MiddlewareOrder(5)]
public sealed class Mw1 : IPacketMiddleware<DemoPacket>
{
    public ValueTask InvokeAsync(IPacketContext<DemoPacket> context, Func<CancellationToken, ValueTask> next) => next(context.CancellationToken);
}

[MiddlewareOrder(5)]
public sealed class Mw2 : IPacketMiddleware<DemoPacket>
{
    public ValueTask InvokeAsync(IPacketContext<DemoPacket> context, Func<CancellationToken, ValueTask> next) => next(context.CancellationToken);
}

public static class Setup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<DemoPacket>().WithMiddleware(new Mw1());
        _ = new PacketDispatchOptions<DemoPacket>().WithMiddleware(new Mw2());
    }
}
""";
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }
}
