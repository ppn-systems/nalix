// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class RequestOptionsAnalyzerTests
{
    [Fact]
    public async Task Analyze_RequestOptionsWithNegativeRetry_ReportsNalix027()
    {
        const string source = """
namespace Demo;
using Nalix.SDK.Options;

public static class RequestOptionUsage
{
    public static void Build()
    {
        _ = RequestOptions.Default.WithRetry(-1);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX027");
    }

    [Fact]
    public async Task Analyze_RequestOptionsWithNegativeTimeout_ReportsNalix028()
    {
        const string source = """
namespace Demo;
using Nalix.SDK.Options;

public static class RequestOptionUsage
{
    public static void Build()
    {
        _ = RequestOptions.Default.WithTimeout(-10);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX028");
    }

    [Fact]
    public async Task Analyze_RequestAsyncInfiniteTimeoutWithRetry_ReportsNalix057()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public static class RequestUsage
{
    public static void Call(IClientConnection client, DemoPacket packet)
    {
        _ = client.RequestAsync<DemoPacket>(
            packet,
            RequestOptions.Default
                .WithTimeout(0)
                .WithRetry(2));
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX057");
    }

    [Fact]
    public async Task Analyze_RequestAsyncEncryptOnTcpSession_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

public sealed class DemoPacket : PacketBase<DemoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new DemoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<DemoPacket>.Deserialize(buffer);
}

public sealed class DemoTcpSession : TcpSession
{
}

public static class RequestUsage
{
    public static void Call(DemoTcpSession client, DemoPacket packet)
    {
        _ = client.RequestAsync<DemoPacket>(packet, RequestOptions.Default.WithEncrypt(true));
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_WithRetryNegativeValue_ReportsNalix027()
    {
        const string source = """
namespace Demo;
using Nalix.SDK.Options;

public sealed class Example
{
    public void Run()
    {
        _ = RequestOptions.Default.WithRetry(-1);
    }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyAnalyzerAsync(source, "NALIX027");
    }

    [Fact]
    public async Task Analyze_WithTimeoutNegativeValue_ReportsNalix028()
    {
        const string source = """
namespace Demo;
using Nalix.SDK.Options;

public sealed class Example
{
    public void Run()
    {
        _ = RequestOptions.Default.WithTimeout(-500);
    }
}
""";

        await Verifier<ConfigurationIgnoreCodeFixProvider>.VerifyAnalyzerAsync(source, "NALIX028");
    }
}
