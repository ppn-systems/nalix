// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using Nalix.Analyzers.Generators;
using Xunit;

namespace Nalix.Analyzers.Tests;

/// <summary>
/// Layer 1 (generator-output) tests for <see cref="PacketSchemaGenerator"/>.
/// </summary>
public sealed class PacketSchemaGeneratorOutputTests
{
    private const string PartialPacketSource = """
        using Nalix.Abstractions.Networking.Packets;
        using Nalix.Codec.DataFrames;

        namespace GenHarness.Schema;

        [Packet]
        public sealed partial class SchemaPacket : PacketBase<SchemaPacket>, IPacketStaticOpcode
        {
            public static ushort StaticOpCode => 6001;
        }
        """;

    private const string NonPartialPacketSource = """
        using Nalix.Abstractions.Networking.Packets;
        using Nalix.Codec.DataFrames;

        namespace GenHarness.Schema;

        [Packet]
        public sealed class NotPartialPacket : PacketBase<NotPartialPacket>, IPacketStaticOpcode
        {
            public static ushort StaticOpCode => 6002;
        }
        """;

    [Fact]
    public void Generator_EmitsLengthAndResetForPool_ForPartialPacket()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new PacketSchemaGenerator(), PartialPacketSource);

        Assert.Empty(result.GeneratorDiagnostics);

        (string HintName, string Text) packetSource = result.GeneratedSources
            .Single(static s => s.HintName.Contains("SchemaPacket"));
        Assert.Contains("override", packetSource.Text);
        Assert.Contains("Length", packetSource.Text);
    }

    [Fact]
    public void Generator_IsDeterministic_AcrossTwoRuns() =>
        GeneratorDriverHarness.AssertDeterministic(new PacketSchemaGenerator(), PartialPacketSource);

    [Fact]
    public void Generator_ReportsNALIX060_ForNonPartialPacketClass_NotSilentSkip()
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new PacketSchemaGenerator(), NonPartialPacketSource);

        string[] ids = [.. result.GeneratorDiagnostics.Select(static d => d.Id)];
        Assert.Contains("NALIX060", ids);

        // A missing partial modifier must not be silently dropped: no schema source should be
        // emitted for this type despite the diagnostic being the only observable signal.
        Assert.DoesNotContain(result.GeneratedSources, static s => s.Text.Contains("NotPartialPacket"));
    }

    [Fact]
    public void Generator_ResetForPool_ResetsSerializeIgnoreMembers_AndPreservesSkipCleanMembers()
    {
        const string source = """
            using Nalix.Abstractions.Networking.Packets;
            using Nalix.Abstractions.Pooling;
            using Nalix.Codec.DataFrames;
            using Nalix.Codec.Serialization;

            namespace GenHarness.Schema;

            [Packet]
            [SerializePackable(SerializeLayout.Explicit)]
            public sealed partial class PoolSafetyPacket : PacketBase<PoolSafetyPacket>, IPacketStaticOpcode
            {
                public static ushort StaticOpCode => 6003;

                [SerializeOrder(0)]
                public int WireValue { get; set; }

                [SerializeIgnore]
                public string? CachedTag { get; set; }

                [SerializeIgnore]
                private int _transientCounter;

                [SerializeOrder(1)]
                [SkipClean]
                public int PreservedSlot { get; set; }
            }
            """;

        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new PacketSchemaGenerator(), source);

        Assert.Empty(result.GeneratorDiagnostics);

        (string HintName, string Text) packetSource = result.GeneratedSources
            .Single(static s => s.HintName.Contains("PoolSafetyPacket"));

        Assert.Contains("this.WireValue = default;", packetSource.Text);
        Assert.Contains("this.CachedTag = string.Empty;", packetSource.Text);
        Assert.Contains("this._transientCounter = default;", packetSource.Text);
        Assert.DoesNotContain("this.PreservedSlot =", packetSource.Text);
    }
}
