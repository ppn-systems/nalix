// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class PacketSelfTypeCodeFixTests
{
    [Fact]
    public async Task Fix_PacketBaseWithWrongSelfType_UsesContainingType()
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

        await AnalyzerTestHarness.AssertCodeFixAsync<PacketSelfTypeCodeFixProvider>(
            source,
            fixedSource,
            diagnosticId: "NALIX010");
    }

    [Fact]
    public async Task Fix_DeserializerWithWrongSelfType_UsesContainingType()
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

        await AnalyzerTestHarness.AssertCodeFixAsync<PacketSelfTypeCodeFixProvider>(
            source,
            fixedSource,
            diagnosticId: "NALIX011");
    }

    [Fact]
    public async Task Fix_PacketBaseSelfTypeMismatch_ReplacesTypeArgument()
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
    public async Task Fix_DeserializerSelfTypeMismatch_ReplacesTypeArgument()
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
}
