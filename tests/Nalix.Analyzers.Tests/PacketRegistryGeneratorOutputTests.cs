// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using Nalix.Analyzers.Generators;
using Xunit;

namespace Nalix.Analyzers.Tests;

/// <summary>
/// Layer 1 (generator-output) tests for <see cref="PacketRegistryGenerator"/>: runs the real
/// generator via <see cref="Microsoft.CodeAnalysis.CSharpGeneratorDriver"/> and asserts the
/// resulting compilation is clean and generation is deterministic.
/// </summary>
public sealed class PacketRegistryGeneratorOutputTests
{
    private const string Source = """
        using Nalix.Abstractions.Networking.Packets;
        using Nalix.Codec.DataFrames;

        namespace GenHarness.Registry;

        [Packet]
        public sealed partial class PingPacket : PacketBase<PingPacket>, IPacketStaticOpcode
        {
            public static ushort StaticOpCode => 5001;
        }

        [Packet]
        public sealed partial class PongPacket : PacketBase<PongPacket>, IPacketStaticOpcode
        {
            public static ushort StaticOpCode => 5002;
        }
        """;

    [Fact]
    public void Generator_ProducesOneFile_AndCompilesCleanly()
    {
        // PacketSchemaGenerator supplies the Length/ResetForPool overrides that PacketBase<T>
        // requires; it must run alongside PacketRegistryGenerator for the resulting compilation
        // (which includes the registry's typeof(T)/Deserialize references) to be error-free.
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(
            [new PacketRegistryGenerator(), new PacketSchemaGenerator()], Source);

        Assert.Empty(result.GeneratorDiagnostics);

        (string HintName, string Text)[] registryOutputs =
            [.. result.GeneratedSources.Where(static s => s.HintName.Contains("PacketRegistryGenerated"))];
        Assert.Single(registryOutputs);
        Assert.Contains("PingPacket", registryOutputs[0].Text);
        Assert.Contains("PongPacket", registryOutputs[0].Text);

        GeneratorDriverHarness.AssertNoCompileErrors(result);
    }

    [Fact]
    public void Generator_IsDeterministic_AcrossTwoRuns() =>
        GeneratorDriverHarness.AssertDeterministic(
            [new PacketRegistryGenerator(), new PacketSchemaGenerator()], Source);

    [Fact]
    public void Generator_ProducesNothing_WhenNoPacketAttributePresent()
    {
        const string source = """
            namespace GenHarness.Registry.Empty;

            public sealed class NotAPacket
            {
            }
            """;

        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new PacketRegistryGenerator(), source);

        Assert.Empty(result.GeneratedSources);
        Assert.Empty(result.GeneratorDiagnostics);
    }

    [Fact]
    public void Generator_ReportsNALIX068_WhenTwoPacketsShareSameStaticOpCode()
    {
        const string duplicateOpCodeSource = """
            using Nalix.Abstractions.Networking.Packets;
            using Nalix.Codec.DataFrames;

            namespace GenHarness.Registry.Collision;

            [Packet]
            public sealed partial class FirstPacket : PacketBase<FirstPacket>, IPacketStaticOpcode
            {
                public static ushort StaticOpCode => 7777;
            }

            [Packet]
            public sealed partial class SecondPacket : PacketBase<SecondPacket>, IPacketStaticOpcode
            {
                public static ushort StaticOpCode => 7777;
            }
            """;

        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(
            [new PacketRegistryGenerator(), new PacketSchemaGenerator()],
            duplicateOpCodeSource);

        string[] ids = [.. result.GeneratorDiagnostics.Select(static d => d.Id)];
        Assert.Contains("NALIX068", ids);

        (string HintName, string Text) registryOutput = result.GeneratedSources
            .Single(static s => s.HintName.Contains("PacketRegistryGenerated"));
        Assert.Contains("FirstPacket", registryOutput.Text);
        Assert.DoesNotContain("SecondPacket", registryOutput.Text);
    }
}
