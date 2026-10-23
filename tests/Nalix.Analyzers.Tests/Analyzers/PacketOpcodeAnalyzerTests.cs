// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class PacketOpcodeAnalyzerTests
{
    [Fact]
    public async Task Analyze_ControllerWithOpcode0x0001_ReportsNalix035()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

[PacketHandler]
public sealed class MyController
{
    [PacketOpcode(0x0001)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context) { }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX035");
    }

    [Fact]
    public async Task Analyze_SameOpcodeInTwoSourceFiles_ReportsNalix036()
    {
        const string source1 = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

[PacketHandler]
public sealed class Controller1
{
    [PacketOpcode(0x0200)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context) { }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        const string source2 = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

[PacketHandler]
public sealed class Controller2
{
    [PacketOpcode(0x0200)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context) { }
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyAnalyzerAsync(
            [source1, source2],
            "NALIX036");
    }

    [Fact]
    public async Task Analyze_OpcodeOutsideReservedRange_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

[PacketHandler]
public sealed class MyController
{
    [PacketOpcode(0x0150)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context) { }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task Analyze_OpcodeDocCommentMismatch_ReportsNalix038()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

[PacketHandler]
public sealed class MyController
{
    /// <summary>
    /// Authenticates a user. OpCode: 0x0150
    /// </summary>
    [PacketOpcode(0x0300)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context) { }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX038");
    }

    [Fact]
    public async Task Analyze_PacketOpcodeOnNonControllerType_ReportsNalix050()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public sealed class NotAController
{
    [PacketOpcode(0x1234)]
    public void Handle(DemoPacket packet, IConnection connection)
    {
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX050");
    }

    [Fact]
    public async Task Analyze_ControllerWithOpcode0x000A_ReportsNalix035()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

[PacketHandler]
public sealed class DemoController
{
    [PacketOpcode(0x000A)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context)
    {
    }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX035");
    }

    [Fact]
    public async Task Analyze_ControllerWithDuplicateOpcodes_ReportsNalix001()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

[PacketHandler]
public sealed class DemoController
{
    [PacketOpcode(0x1201)]
    public void HandleA(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) { }

    [PacketOpcode(0x1201)]
    public void HandleB(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) { }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX001", "NALIX001");
    }

    [Fact]
    public async Task Analyze_HandlerMissingPacketOpcode_ReportsNalix002()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

[PacketHandler]
public sealed class DemoController
{
    public void Handle(DemoPacket packet, IConnection connection) { }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX002");
    }

    [Fact]
    public async Task Analyze_SameOpcodeAcrossControllers_ReportsNalix036()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

[PacketHandler]
public sealed class ControllerA
{
    [PacketOpcode(0x1234)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) { }
}

[PacketHandler]
public sealed class ControllerB
{
    [PacketOpcode(0x1234)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) { }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX036");
    }

    [Fact]
    public async Task Analyze_OpcodeDocumentationMismatch_ReportsNalix038()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

[PacketHandler]
public sealed class ControllerA
{
    /// <summary>Handles packet opcode 0x8888.</summary>
    [PacketOpcode(0x1241)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) { }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX038");
    }
}
