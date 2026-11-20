// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
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

    private static string Handler(string attributes) => $$"""
        using System.Threading.Tasks;
        using Nalix.Abstractions.Networking.Packets;
        using Nalix.Abstractions.Networking.Protocols;
        using Nalix.Abstractions.Security;
        using Nalix.Abstractions;
        using Nalix.Abstractions.Networking;
        using Nalix.Abstractions.Networking.Packets;
        using Nalix.Codec.DataFrames;

        namespace GenHarness.Handlers;

        [Packet]
        public sealed partial class TestPacket : PacketBase<TestPacket>, IPacketStaticOpcode
        {
            public static ushort StaticOpCode => 0x00FD;
        }

        [PacketHandler("Harness")]
        public sealed class HarnessHandlers
        {
            {{attributes}}
            [PacketOpcode(0x00FE)]
            public static ValueTask<TestPacket> HandleAsync(IPacketContext<TestPacket> context) => default;
        }
        """;

    private static string GeneratedHandler(string source)
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(
            [new PacketHandlerGenerator(), new PacketSchemaGenerator(), new PacketRegistryGenerator()], source);
        GeneratorDriverHarness.AssertNoCompileErrors(result);
        return Assert.Single(result.GeneratedSources.Where(static s => s.HintName.Contains("PacketHandler"))).Text;
    }

    [Fact]
    public void Generate_RateLimitNamedArguments_AreForwarded()
    {
        string text = GeneratedHandler(Handler(
            """[PacketRateLimit(5, 2, PolicyId = "auth", HardLockoutSeconds = 900, MaxSoftViolations = 2)]"""));

        Assert.Contains("PacketRateLimitAttribute(5, 2) { PolicyId = \"auth\", HardLockoutSeconds = 900, MaxSoftViolations = 2 }", text);
    }

    [Fact]
    public void Generate_RateLimitFractionalBurst_IsInvariantCultureUnderCommaLocale()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string text = GeneratedHandler(Handler("[PacketRateLimit(5, 1.5)]"));

            Assert.Contains("PacketRateLimitAttribute(5, 1.5)", text);
            Assert.DoesNotContain("1,5", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Generate_BareEncryptionAttribute_IsForwardedAsEnabled()
    {
        string text = GeneratedHandler(Handler("[PacketEncryption]"));

        Assert.Contains("encryption: new global::Nalix.Abstractions.Networking.Packets.PacketEncryptionAttribute(true", text);
        Assert.DoesNotContain("encryption: null", text);
    }

    [Fact]
    public void Generate_EncryptionAlgorithmType_IsForwarded()
    {
        string text = GeneratedHandler(Handler("[PacketEncryption(true, CipherSuiteType.Chacha20Poly1305)]"));

        Assert.Contains("PacketEncryptionAttribute(true, (global::Nalix.Abstractions.Security.CipherSuiteType)", text);
    }

    [Fact]
    public void Generate_TimeoutAndDelivery_AreForwarded()
    {
        string text = GeneratedHandler(Handler(
            "[PacketTimeout(1500)]\n            [PacketDelivery(Nalix.Abstractions.Networking.Datagrams.DeliveryMethod.Unreliable, 2)]"));

        Assert.Contains("PacketTimeoutAttribute((int)1500)", text);
        Assert.Contains("PacketDeliveryAttribute((global::Nalix.Abstractions.Networking.Datagrams.DeliveryMethod)0, 2)", text);
    }
}
