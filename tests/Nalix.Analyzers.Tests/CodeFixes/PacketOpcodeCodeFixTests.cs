// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class PacketOpcodeCodeFixTests
{
    [Fact]
    public async Task Fix_HandlerWithoutPacketOpcode_AddsPacketOpcode()
    {
        const string source = """
namespace Demo;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;

[PacketHandler]
public sealed class ExampleController
{
    public Task Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context)
    {
        return Task.CompletedTask;
    }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        const string fixedSource = """
namespace Demo;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;

[PacketHandler]
public sealed class ExampleController
{
    [PacketOpcode(0x0000)]
    public Task Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context)
    {
        return Task.CompletedTask;
    }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX002",
            actionIndex: 0,
            expectedTitle: "Add [PacketOpcode(...)]",
            expectedEquivalenceKey: "Nalix.PacketOpcode.Add");
    }
}
