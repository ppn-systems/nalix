// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class PacketHandlerAnalyzerTests
{
    [Fact]
    public async Task Analyze_HandlerAllocatingInHotPath_ReportsNalix037()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

[PacketHandler]
public sealed class MyController
{
    [PacketOpcode(0x0200)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context) 
    { 
        var x = new object(); // Allocation in hot path
    }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX037");
    }

    [Fact]
    public async Task Analyze_ExtraParameterWithFromScope_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Injection;

public interface IMyService { }

[PacketHandler]
public sealed class MyController
{
    [PacketOpcode(0x0200)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context, [FromScope] IMyService service) { }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task Analyze_ExtraParameterWithoutFromScope_ReportsNalix003()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;

public interface IMyService { }

[PacketHandler]
public sealed class MyController
{
    [PacketOpcode(0x0200)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context, IMyService service) { }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX003");
    }

    [Fact]
    public async Task Analyze_ValueTypeParameterWithFromScope_ReportsNalix003()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Injection;

[PacketHandler]
public sealed class MyController
{
    [PacketOpcode(0x0200)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<LoginPacket> context, [FromScope] int invalidValueType) { }
}

public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

        await Verifier<PacketOpcodeCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX003");
    }

    [Fact]
    public async Task Analyze_RegisteredControllerWithoutPacketHandler_ReportsNalix008AndNalix050()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public sealed class PlainController
{
    [PacketOpcode(0x1200)]
    public void Handle(DemoPacket packet, IConnection connection)
    {
    }
}

public static class DispatchSetup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<DemoPacket>().WithHandler<PlainController>();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX008", "NALIX050");
    }

    [Fact]
    public async Task Analyze_HandlerWithInvalidSignature_ReportsNalix003()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

[PacketHandler]
public sealed class DemoController
{
    [PacketOpcode(0x1202)]
    public void Handle(DemoPacket packet) { }
}

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX003");
    }

    [Fact]
    public async Task Analyze_AllocationInHandler_ReportsNalix037()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;

public sealed class Helper { }

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

[PacketHandler]
public sealed class ControllerA
{
    [PacketOpcode(0x1240)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context)
    {
        _ = new Helper();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX037");
    }

    [Fact]
    public async Task Analyze_HandlerWithUnsupportedReturnType_ReportsNalix048()
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
    [PacketOpcode(0x1242)]
    public int Handle(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) => 1;
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX048");
    }

    [Fact]
    public async Task Analyze_DuplicatePacketHandlerName_ReportsNalix054()
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

[PacketHandler("dup")]
public sealed class ControllerA
{
    [PacketOpcode(0x1243)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) { }
}

[PacketHandler("dup")]
public sealed class ControllerB
{
    [PacketOpcode(0x1244)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) { }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX054");
    }

    [Fact]
    public async Task Analyze_RedundantContextPacketCast_ReportsNalix055()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

[PacketHandler]
public sealed class ControllerA
{
    [PacketOpcode(0x1245)]
    public void Handle(PacketContext<DemoPacket> context)
    {
        _ = (DemoPacket)context.Packet;
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX055");
    }

    [Fact]
    public async Task Analyze_GenericHandlerMethod_ReportsNalix058()
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
    [PacketOpcode(0x1246)]
    public void Handle<T>(Nalix.Runtime.Dispatching.PacketContext<DemoPacket> context) { }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX058");
    }

    [Fact]
    public async Task Analyze_HandlerContextPacketTypeMismatch_ReportsNalix004()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public sealed class OtherPacket : PacketBase<OtherPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new OtherPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<OtherPacket>.Deserialize(buffer);
}

[PacketHandler]
public sealed class DemoController
{
    [PacketOpcode(0x1250)]
    public void Handle(PacketContext<OtherPacket> context) { }
}

public static class Setup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<DemoPacket>().WithHandler<DemoController>();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX004");
    }

    [Fact]
    public async Task Analyze_HandlerLegacyContextTypeMismatch_ReportsNalix004()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Network.Routing; using Nalix.Network.Routing; using Nalix.Runtime.Dispatching;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public sealed class OtherPacket : PacketBase<OtherPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new OtherPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<OtherPacket>.Deserialize(buffer);
}

[PacketHandler]
public sealed class DemoController
{
    [PacketOpcode(0x1251)]
    public void Handle(Nalix.Runtime.Dispatching.PacketContext<OtherPacket> context) { }
}

public static class Setup
{
    public static void Configure()
    {
        _ = new PacketDispatchOptions<DemoPacket>().WithHandler<DemoController>();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX004");
    }
}
