// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class SdkUsageAnalyzerTests
{
    private const string PacketTypes = """
public sealed class PingPacket : PacketBase<PingPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9001;

    public static new PingPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<PingPacket>.Deserialize(buffer);
}

[PacketIdempotent]
public sealed class ReadPacket : PacketBase<ReadPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9002;

    public static new ReadPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<ReadPacket>.Deserialize(buffer);
}

public sealed class DerivedReadPacket : PacketBase<DerivedReadPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9003;

    public static new DerivedReadPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DerivedReadPacket>.Deserialize(buffer);
}
""";

    private static string Source(string body) => $$"""
namespace Demo;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

{{PacketTypes}}

public static class Usage
{
    public static async Task Run(IClientConnection client, PingPacket ping, ReadPacket read, IPacket anyPacket)
    {
        {{body}}
    }
}
""";

    // ─── NALIX115 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("_ = await client.RequestAsync<PingPacket>(ping, RequestOptions.Default.WithRetry(2));")]
    [InlineData("_ = await client.RequestAsync<PingPacket>(ping, new RequestOptions { RetryCount = 3 });")]
    [InlineData("_ = await client.RequestAsync<PingPacket>(ping, RequestOptions.Default.WithTimeout(1000).WithRetry(1));")]
    public async Task Analyze_RetryOnNonIdempotentPacket_ReportsNalix115(string call)
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([Source(call), PolicyTestSources.Stubs], "NALIX115");
    }

    [Theory]
    [InlineData("_ = await client.RequestAsync<ReadPacket>(read, RequestOptions.Default.WithRetry(2));")]
    [InlineData("_ = await client.RequestAsync<PingPacket>(ping, RequestOptions.Default.WithRetry(0));")]
    [InlineData("_ = await client.RequestAsync<PingPacket>(ping, RequestOptions.Default);")]
    [InlineData("_ = await client.RequestAsync<PingPacket>(ping);")]
    [InlineData("_ = await client.RequestAsync<PingPacket>(ping, null);")]
    [InlineData("_ = await client.RequestAsync<PingPacket>(anyPacket, RequestOptions.Default.WithRetry(2));")]
    public async Task Analyze_RequestWithoutRiskyRetry_ReportsNoDiagnostic(string call)
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([Source(call), PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_RetryWithNonConstantCount_ReportsNoDiagnostic()
    {
        const string body = """
        int retries = System.Environment.TickCount;
        _ = await client.RequestAsync<PingPacket>(ping, RequestOptions.Default.WithRetry(retries));
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([Source(body), PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_IdempotentAttributeIsNotInherited_ReportsNalix115ForDerivedPacket()
    {
        const string body = """
        DerivedReadPacket derived = default!;
        _ = await client.RequestAsync<DerivedReadPacket>(derived, RequestOptions.Default.WithRetry(2));
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([Source(body), PolicyTestSources.Stubs], "NALIX115");
    }

    // ─── NALIX116 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("client.On<PingPacket>(p => { });")]
    [InlineData("_ = client.On<PingPacket>(p => { });")]
    [InlineData("client.OnExact<PingPacket>(p => { });")]
    [InlineData("client.SubscribeTemp<PingPacket>(p => { });")]
    [InlineData("_ = client.SubscribeTemp<PingPacket>(p => { });")]
    public async Task Analyze_DiscardedSubscription_ReportsNalix116(string call)
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([Source(call + "\n        await Task.CompletedTask;"), PolicyTestSources.Stubs], "NALIX116");
    }

    [Theory]
    [InlineData("using IDisposable subscription = client.On<PingPacket>(p => { });")]
    [InlineData("IDisposable subscription = client.On<PingPacket>(p => { }); subscription.Dispose();")]
    [InlineData("var subscription = client.OnExact<PingPacket>(p => { }); _ = subscription;")]
    [InlineData("client.On<PingPacket>(p => { }).Dispose();")]
    [InlineData("using (client.SubscribeTemp<PingPacket>(p => { })) { }")]
    public async Task Analyze_RetainedSubscription_ReportsNoDiagnostic(string call)
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([Source(call + "\n        await Task.CompletedTask;"), PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_OneShotSubscriptionDiscarded_ReportsNoDiagnostic()
    {
        // OnOnce removes itself after the first matching packet, so dropping the handle is by design.
        const string body = """
#pragma warning disable CS0618
        client.OnOnce<PingPacket>(p => true, p => { });
#pragma warning restore CS0618
        await Task.CompletedTask;
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([Source(body), PolicyTestSources.Stubs]);
    }

    [Fact]
    public async Task Analyze_UnrelatedMethodNamedOn_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;

public sealed class Bus
{
    public IDisposable On(Action handler) => new Handle();

    private sealed class Handle : IDisposable
    {
        public void Dispose() { }
    }
}

public static class Usage
{
    public static void Run(Bus bus) => bus.On(() => { });
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync([source, PolicyTestSources.Stubs]);
    }
}
