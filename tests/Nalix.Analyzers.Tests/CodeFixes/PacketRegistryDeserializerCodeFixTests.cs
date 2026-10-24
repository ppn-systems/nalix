// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class PacketRegistryDeserializerCodeFixTests
{
    [Fact]
    public async Task Fix_RegisteredPacketWithoutDeserialize_AddsDeserializer()
    {
        const string source = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class RegistryPacket : PacketBase<RegistryPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

}

public sealed class Example
{
    public void Run()
    {
        new PacketRegistryFactory().RegisterPacket<RegistryPacket>();
    }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Codec.DataFrames;

public sealed class RegistryPacket : PacketBase<RegistryPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new RegistryPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<RegistryPacket>.Deserialize(buffer);
}

public sealed class Example
{
    public void Run()
    {
        new PacketRegistryFactory().RegisterPacket<RegistryPacket>();
    }
}
""";

        await Verifier<PacketRegistryDeserializerCodeFixProvider>.VerifyCodeFixWithSyntheticDiagnosticAsync(
            source,
            fixedSource,
            "NALIX009",
            "Packet type 'RegistryPacket' is registered with PacketRegistryFactory.RegisterPacket, but it does not implement IPacketDeserializer<RegistryPacket>.",
            "RegisterPacket<RegistryPacket>",
            actionIndex: 0,
            expectedTitle: "Add Deserialize(ReadOnlySpan<byte>)",
            expectedEquivalenceKey: "Nalix.PacketRegistry.Deserialize.Add");
    }
}
