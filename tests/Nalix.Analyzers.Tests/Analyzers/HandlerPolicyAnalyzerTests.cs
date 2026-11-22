// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class HandlerPolicyAnalyzerTests
{
    private const string Packet = """
public sealed class LoginPacket : Nalix.Codec.DataFrames.PacketBase<LoginPacket>
{
    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}
""";

    // ─── NALIX111 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_HandlerWithoutPermission_ReportsNalix111()
    {
        const string source = """
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""" + "\n" + Packet;

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([source, PolicyTestSources.Stubs], "NALIX111");
    }

    [Fact]
    public async Task Analyze_InternalHandlerWithoutPermission_ReportsNalix111()
    {
        const string source = """
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    internal Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""" + "\n" + Packet;

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([source, PolicyTestSources.Stubs], "NALIX111");
    }

    [Theory]
    [InlineData("[PacketPermission(Nalix.Abstractions.Security.PermissionLevel.USER)]")]
    [InlineData("[PacketPermission(Nalix.Abstractions.Security.PermissionLevel.NONE)]")]
    [InlineData("[PacketPermission]")]
    public async Task Analyze_HandlerWithPermission_ReportsNoDiagnostic(string permissionAttribute)
    {
        string source = $$"""
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    {{permissionAttribute}}
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""" + "\n" + Packet;

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([source, PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_NonHandlerMethodWithoutPermission_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    [PacketPermission]
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;

    public int Helper(int value) => value + 1;

    private Task Internal(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""" + "\n" + Packet;

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([source, PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_OpcodeMethodOutsidePacketHandlerType_DoesNotReportNalix111()
    {
        const string source = """
namespace Demo;

public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""" + "\n" + Packet;

        // NALIX050 (opcode on a non-controller) is the only finding: the generator never builds metadata here.
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([source, PolicyTestSources.Stubs], "NALIX050");
    }

    // ─── NALIX112 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[PacketRateLimit(0)]", 1)]
    [InlineData("[PacketRateLimit(-5, 2)]", 1)]
    [InlineData("[PacketRateLimit(10, 0)]", 1)]
    [InlineData("[PacketRateLimit(10, -1.5)]", 1)]
    [InlineData("[PacketRateLimit(0, 0)]", 2)]
    [InlineData("[PacketRateLimit(10)]", 0)]
    [InlineData("[PacketRateLimit(10, 0.5)]", 0)]
    [InlineData("[PacketRateLimit(10, 2, PolicyId = \"login\")]", 0)]
    public async Task Analyze_PacketRateLimit_ReportsNalix112OnlyForNonPositiveValues(string attribute, int expected)
    {
        string source = $$"""
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    [PacketPermission]
    {{attribute}}
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""" + "\n" + Packet;

        string[] ids = [.. System.Linq.Enumerable.Repeat("NALIX112", expected)];
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([source, PolicyTestSources.Stubs], ids);
    }

    // ─── NALIX113 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[PacketTimeout(0)]", 1)]
    [InlineData("[PacketTimeout(-1)]", 1)]
    [InlineData("[PacketTimeout(250)]", 0)]
    public async Task Analyze_PacketTimeout_ReportsNalix113OnlyForNonPositiveValues(string attribute, int expected)
    {
        string source = $$"""
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    [PacketPermission]
    {{attribute}}
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""" + "\n" + Packet;

        string[] ids = [.. System.Linq.Enumerable.Repeat("NALIX113", expected)];
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([source, PolicyTestSources.Stubs], ids);
    }

    // ─── NALIX114 ────────────────────────────────────────────────────────────

    private const string HostWithoutMiddleware = """
namespace Demo;
using Nalix.Hosting;

public static class Host
{
    public static void Run()
    {
        var builder = new NetworkApplicationBuilder();
        builder.UseBufferPoolManager();
        builder.UseConnectionHub();
        builder.ListenTcp<object>();
        _ = builder.Build();
    }
}
""";

    private const string RateLimitedHandler = """
namespace Demo;

[PacketHandler]
public sealed class LoginController
{
    [PacketOpcode(0x0200)]
    [PacketPermission]
    [PacketRateLimit(10)]
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""";

    private const string TimeoutHandler = """
namespace Demo;

[PacketHandler]
public sealed class SlowController
{
    [PacketOpcode(0x0201)]
    [PacketPermission]
    [PacketTimeout(500)]
    public Task Handle(IPacketContext<LoginPacket> context) => Task.CompletedTask;
}
""";

    [Fact]
    public async Task Analyze_RateLimitWithoutMiddlewareInHostProject_ReportsNalix114()
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(
            [RateLimitedHandler + "\n" + Packet, HostWithoutMiddleware, PolicyTestSources.Stubs],
            "NALIX114");
    }

    [Fact]
    public async Task Analyze_TimeoutWithoutMiddlewareInHostProject_ReportsNalix114()
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(
            [TimeoutHandler + "\n" + Packet, HostWithoutMiddleware, PolicyTestSources.Stubs],
            "NALIX114");
    }

    [Fact]
    public async Task Analyze_PolicyAttributesWithoutHost_ReportsNoDiagnostic()
    {
        // A handler library cannot see the middleware its host registers.
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(
            [RateLimitedHandler + "\n" + Packet, PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_RateLimitWithMiddlewareRegistered_ReportsNoDiagnostic()
    {
        const string registration = """
namespace Demo;
using Nalix.Network.Routing;
using Nalix.Runtime.Middleware.Standard;

public static class Registration
{
    public static void Configure(PacketDispatchOptions<IPacket> options) => options.WithMiddleware(new RateLimitMiddleware());
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(
            [RateLimitedHandler + "\n" + Packet, HostWithoutMiddleware, registration, PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_RateLimitWithDerivedMiddlewareRegistered_ReportsNoDiagnostic()
    {
        const string registration = """
namespace Demo;
using Nalix.Network.Routing;
using Nalix.Runtime.Middleware.Standard;

[Nalix.Abstractions.Middleware.MiddlewareOrder(-39)]
public sealed class CustomRateLimit : RateLimitMiddleware;

public static class Registration
{
    public static void Configure(PacketDispatchOptions<IPacket> options) => options.WithMiddleware(new CustomRateLimit());
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(
            [RateLimitedHandler + "\n" + Packet, HostWithoutMiddleware, registration, PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_OnlyTheMissingMiddlewareIsReported()
    {
        const string registration = """
namespace Demo;
using Nalix.Network.Routing;
using Nalix.Runtime.Middleware.Standard;

public static class Registration
{
    public static void Configure(PacketDispatchOptions<IPacket> options) => options.WithMiddleware(new RateLimitMiddleware());
}
""";

        // Rate limiting is registered, timeout is not: one NALIX114, for the timeout handler.
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(
            [RateLimitedHandler + "\n" + Packet, TimeoutHandler, HostWithoutMiddleware, registration, PolicyTestSources.Stubs],
            "NALIX114");
    }
}
