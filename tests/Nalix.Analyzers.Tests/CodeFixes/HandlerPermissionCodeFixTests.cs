// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class HandlerPermissionCodeFixTests
{
    [Fact]
    public async Task Fix_HandlerWithoutPermission_AddsPacketPermissionAfterOpcode()
    {
        const string source = """
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        const string fixedSource = """
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    [PacketPermission(global::Nalix.Abstractions.Security.PermissionLevel.USER)]
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<HandlerPermissionCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX111",
            actionIndex: 0,
            expectedTitle: "Add [PacketPermission(PermissionLevel.USER)]",
            expectedEquivalenceKey: "Nalix.PacketPermission.Add");
    }
}
