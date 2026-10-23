// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class RequestOptionsConsistencyCodeFixTests
{
    [Fact]
    public async Task Fix_InfiniteTimeoutWithObjectInitializerRetry_SetsRetryCountToZero()
    {
        const string source = """
namespace Demo;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

public sealed class LoginPacket : PacketBase<LoginPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

public sealed class Client : TcpSession
{
}

public sealed class Example
{
    public Task<LoginPacket> Run(Client client, LoginPacket packet)
        => RequestExtensions.RequestAsync<LoginPacket>(client, packet, new RequestOptions { TimeoutMs = 0, RetryCount = 3 });
}
""";

        const string fixedSource = """
namespace Demo;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

public sealed class LoginPacket : PacketBase<LoginPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

public sealed class Client : TcpSession
{
}

public sealed class Example
{
    public Task<LoginPacket> Run(Client client, LoginPacket packet)
        => RequestExtensions.RequestAsync<LoginPacket>(client, packet, new RequestOptions { TimeoutMs = 0, RetryCount = 0 });
}
""";

        await Verifier<RequestOptionsConsistencyCodeFixProvider>.VerifyCodeFixWithSyntheticDiagnosticAsync(
            source,
            fixedSource,
            "NALIX057",
            "RequestOptions sets TimeoutMs=0 with RetryCount=3; retries are typically ineffective with infinite timeout.",
            "RequestExtensions.RequestAsync<LoginPacket>",
            actionIndex: 0,
            expectedTitle: "Set RetryCount to 0",
            expectedEquivalenceKey: "Nalix.RequestOptions.Retry.SetZero");
    }

    [Fact]
    public async Task Fix_InfiniteTimeoutWithFluentRetry_SetsWithRetryArgumentToZero()
    {
        const string source = """
namespace Demo;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

public sealed class LoginPacket : PacketBase<LoginPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

public sealed class Client : TcpSession
{
}

public sealed class Example
{
    public Task<LoginPacket> Run(Client client, LoginPacket packet)
        => RequestExtensions.RequestAsync<LoginPacket>(client, packet, RequestOptions.Default.WithTimeout(0).WithRetry(2));
}
""";

        const string fixedSource = """
namespace Demo;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

public sealed class LoginPacket : PacketBase<LoginPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9999;

    public static new LoginPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<LoginPacket>.Deserialize(buffer);
}

public sealed class Client : TcpSession
{
}

public sealed class Example
{
    public Task<LoginPacket> Run(Client client, LoginPacket packet)
        => RequestExtensions.RequestAsync<LoginPacket>(client, packet, RequestOptions.Default.WithTimeout(0).WithRetry(0));
}
""";

        await Verifier<RequestOptionsConsistencyCodeFixProvider>.VerifyCodeFixWithSyntheticDiagnosticAsync(
            source,
            fixedSource,
            "NALIX057",
            "RequestOptions sets TimeoutMs=0 with RetryCount=2; retries are typically ineffective with infinite timeout.",
            "RequestExtensions.RequestAsync<LoginPacket>",
            actionIndex: 0,
            expectedTitle: "Set RetryCount to 0",
            expectedEquivalenceKey: "Nalix.RequestOptions.Retry.SetZero");
    }
}
