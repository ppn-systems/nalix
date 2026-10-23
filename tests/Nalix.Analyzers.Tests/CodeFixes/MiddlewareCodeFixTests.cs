// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class MiddlewareCodeFixTests
{
    [Fact]
    public async Task Fix_PacketMiddlewareWithoutOrder_AddsMiddlewareOrder()
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

public sealed class DemoMiddleware : IPacketMiddleware<LoginPacket>
{
    public ValueTask InvokeAsync(IPacketContext<LoginPacket> context, Func<CancellationToken, ValueTask> next) => next(CancellationToken.None);
}
""";

        const string fixedSource = """
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
public sealed class DemoMiddleware : IPacketMiddleware<LoginPacket>
{
    public ValueTask InvokeAsync(IPacketContext<LoginPacket> context, Func<CancellationToken, ValueTask> next) => next(CancellationToken.None);
}
""";

        await Verifier<MiddlewareCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX030",
            actionIndex: 0,
            expectedTitle: "Add [MiddlewareOrder(0)]",
            expectedEquivalenceKey: "Nalix.Middleware.Order.AddDefault");
    }

    [Fact]
    public async Task Fix_InboundMiddlewareWithAlwaysExecute_RemovesFlag()
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

        const string fixedSource = """
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
[MiddlewareStage(MiddlewareStage.Inbound)]
public sealed class DemoMiddleware : IPacketMiddleware<LoginPacket>
{
    public ValueTask InvokeAsync(IPacketContext<LoginPacket> context, Func<CancellationToken, ValueTask> next) => next(CancellationToken.None);
}
""";

        await Verifier<MiddlewareCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX032",
            actionIndex: 0,
            expectedTitle: "Remove AlwaysExecute = true",
            expectedEquivalenceKey: "Nalix.Middleware.Stage.RemoveAlwaysExecute");
    }
}
