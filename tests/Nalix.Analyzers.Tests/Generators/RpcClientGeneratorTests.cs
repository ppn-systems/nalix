// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using Nalix.Analyzers.Generators;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Generators;

/// <summary>
/// Layer 1 (generator-output) tests for <see cref="RpcClientGenerator"/>.
/// </summary>
public sealed class RpcClientGeneratorTests
{
    private const string Source = """
        using System.Threading.Tasks;
        using Nalix.Abstractions.Networking.Rpc;
        using Nalix.Abstractions.Networking.Packets;
        using Nalix.Abstractions.Primitives;
        using Nalix.SDK.Transport.Rpc;

        namespace GenHarness.Rpc;

        public struct HarnessRequest : IPacket
        {
            public PacketHeader Header { get; set; }
            public int Length => 0;
            public byte[] Serialize() => [];
            public int Serialize(System.Span<byte> buffer) => 0;
        }

        public class HarnessResponse : IPacket, IPacketStaticOpcode
        {
            public PacketHeader Header { get; set; }
            public int Length => 0;
            public byte[] Serialize() => [];
            public int Serialize(System.Span<byte> buffer) => 0;
            public static ushort StaticOpCode => 9001;
        }

        [RpcService]
        public interface IHarnessRpcService
        {
            ValueTask PingAsync(HarnessRequest request);
            RpcCall<HarnessResponse> RequestAsync(HarnessRequest request);
        }
        """;

    [Fact]
    public void Generate_RpcServiceInterface_EmitsProxyClass()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new RpcClientGenerator(), Source);

        Assert.Empty(result.GeneratorDiagnostics);

        (string HintName, string Text) source = result.GeneratedSources
            .Single(static s => s.HintName.Contains("HarnessRpcService_RpcClient"));
        Assert.Contains("class HarnessRpcService_RpcClient", source.Text);
        Assert.Contains("RegisterFactory", source.Text);

        GeneratorDriverHarness.AssertNoCompileErrors(result);
    }

    [Fact]
    public void Generate_SameSourceTwice_EmitsIdenticalOutput() =>
        GeneratorDriverHarness.AssertDeterministic(new RpcClientGenerator(), Source);

    [Fact]
    public void Generate_TaskOfTReturnType_EmitsMethodBody()
    {
        const string source = """
            using System.Threading.Tasks;
            using Nalix.Abstractions.Networking.Rpc;
            using Nalix.Abstractions.Networking.Packets;
            using Nalix.Abstractions.Primitives;

            namespace GenHarness.Rpc.TaskReturn;

            public struct HarnessRequest : IPacket
            {
                public PacketHeader Header { get; set; }
                public int Length => 0;
                public byte[] Serialize() => [];
                public int Serialize(System.Span<byte> buffer) => 0;
            }

            public class HarnessResponse : IPacket, IPacketStaticOpcode
            {
                public PacketHeader Header { get; set; }
                public int Length => 0;
                public byte[] Serialize() => [];
                public int Serialize(System.Span<byte> buffer) => 0;
                public static ushort StaticOpCode => 9002;
            }

            [RpcService]
            public interface IHarnessTaskRpcService
            {
                Task<HarnessResponse> RequestAsync(HarnessRequest request);
                Task PingAsync(HarnessRequest request);
            }
            """;

        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new RpcClientGenerator(), source);

        Assert.Empty(result.GeneratorDiagnostics);

        (string HintName, string Text) generated = result.GeneratedSources
            .Single(static s => s.HintName.Contains("HarnessTaskRpcService_RpcClient"));
        Assert.Contains(".RequestAsync<", generated.Text);
        Assert.Contains(".AsTask()", generated.Text);
        Assert.Contains("_session.SendAsync(", generated.Text);

        GeneratorDriverHarness.AssertNoCompileErrors(result);
    }

    [Fact]
    public void Generate_UnsupportedReturnType_ReportsNalix066()
    {
        const string source = """
            using Nalix.Abstractions.Networking.Rpc;
            using Nalix.Abstractions.Networking.Packets;
            using Nalix.Abstractions.Primitives;

            namespace GenHarness.Rpc.Unsupported;

            public struct HarnessRequest : IPacket
            {
                public PacketHeader Header { get; set; }
                public int Length => 0;
                public byte[] Serialize() => [];
                public int Serialize(System.Span<byte> buffer) => 0;
            }

            [RpcService]
            public interface IHarnessUnsupportedRpcService
            {
                int Ping(HarnessRequest request);
            }
            """;

        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new RpcClientGenerator(), source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "NALIX066");

        (string HintName, string Text) generated = result.GeneratedSources
            .Single(static s => s.HintName.Contains("HarnessUnsupportedRpcService_RpcClient"));
        Assert.Contains("NotSupportedException", generated.Text);

        GeneratorDriverHarness.AssertNoCompileErrors(result);
    }

    [Fact]
    public void Generate_InterfaceWithoutRpcServiceAttribute_EmitsNothing()
    {
        const string source = """
            namespace GenHarness.Rpc.Empty;

            public interface INotAnRpcService
            {
                void Foo();
            }
            """;

        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new RpcClientGenerator(), source);

        Assert.Empty(result.GeneratedSources);
        Assert.Empty(result.GeneratorDiagnostics);
    }
}
