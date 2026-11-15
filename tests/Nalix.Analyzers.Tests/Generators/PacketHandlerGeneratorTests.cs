// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using Nalix.Analyzers.Generators;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Generators;

/// <summary>
/// Layer 1 (generator-output) tests for <see cref="PacketHandlerGenerator"/>: checks that the permission policy
/// written on a handler method (level and evaluation mode) survives into the generated descriptor.
/// </summary>
public sealed class PacketHandlerGeneratorTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Nalix.Abstractions;
        using Nalix.Abstractions.Networking.Packets;
        using Nalix.Abstractions.Networking.Protocols;
        using Nalix.Abstractions.Security;
        using Nalix.Codec.DataFrames;

        namespace GenHarness.Handlers;

        [PacketHandler("Harness")]
        public sealed class HarnessHandlers
        {
            [PacketPermission(PermissionLevel.OBSERVER, PermissionEvaluation.StrictMatch)]
            [PacketOpcode(0x00FE)]
            public static ValueTask<TestPacket> StrictAsync(IPacketContext<TestPacket> context) => default;

            [PacketPermission(PermissionLevel.USER)]
            [PacketOpcode(0x00FD)]
            public static ValueTask<TestPacket> MinimumAsync(IPacketContext<TestPacket> context) => default;
        }

        public sealed partial class TestPacket : PacketBase<TestPacket>
        {
        }
        """;

    [Fact]
    public void Generate_StrictMatchPermission_ForwardsEvaluationToDescriptor()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new PacketHandlerGenerator(), Source);

        string text = Assert.Single(result.GeneratedSources.Where(static s => s.HintName.Contains("PacketHandler"))).Text;

        Assert.Contains("PacketPermissionAttribute((global::Nalix.Abstractions.Security.PermissionLevel)22, (global::Nalix.Abstractions.Security.PermissionEvaluation)1)", text);
    }

    [Fact]
    public void Generate_SingleArgumentPermission_KeepsMinimumLevelDefault()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new PacketHandlerGenerator(), Source);

        string text = Assert.Single(result.GeneratedSources.Where(static s => s.HintName.Contains("PacketHandler"))).Text;

        Assert.Contains("PacketPermissionAttribute((global::Nalix.Abstractions.Security.PermissionLevel)100)", text);
        Assert.DoesNotContain("PacketPermissionAttribute((global::Nalix.Abstractions.Security.PermissionLevel)100,", text);
    }
}
