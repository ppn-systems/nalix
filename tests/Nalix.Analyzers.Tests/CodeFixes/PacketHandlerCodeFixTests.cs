// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class PacketHandlerCodeFixTests
{
    [Fact]
    public async Task Fix_ControllerWithoutPacketHandler_AddsPacketHandler()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class ExampleController
{
    [PacketOpcode(1)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context)
    {
    }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

public sealed class Example
{
    public void Run()
    {
        new PacketDispatchOptions<LoginPacket>().WithHandler<ExampleController>();
    }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

[PacketHandler]
public sealed class ExampleController
{
    [PacketOpcode(1)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context)
    {
    }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

public sealed class Example
{
    public void Run()
    {
        new PacketDispatchOptions<LoginPacket>().WithHandler<ExampleController>();
    }
}
""";

        await Verifier<PacketHandlerCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX008",
            actionIndex: 0,
            expectedTitle: "Add [PacketHandler]",
            expectedEquivalenceKey: "Nalix.PacketHandler.Add");
    }
}
