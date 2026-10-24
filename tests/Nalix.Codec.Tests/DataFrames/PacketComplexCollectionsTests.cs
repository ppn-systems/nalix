// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Serialization;

namespace Nalix.Codec.Tests.DataFrames;

public sealed partial class PacketComplexCollectionsTests
{
    static PacketComplexCollectionsTests()
    {
        // Pre-register formatters for types used in extreme/edge-case tests.
        // These involve deeply nested generics and project-specific enums
        // that are not covered by the built-in pre-registration.
        FormatterProvider.RegisterAllFormatters<PacketPriority>();
        FormatterProvider.RegisterCollectionFormatters<List<string>>();
        FormatterProvider.RegisterCollectionFormatters<List<List<string>>>();
        FormatterProvider.RegisterDictionary<string, List<int>>();
        FormatterProvider.RegisterCollectionFormatters<Dictionary<string, List<int>>>();
    }

    [Fact]
    public void LengthAndSerialization_WithComplexCollections_MatchesSerializedData()
    {
        // 1. Prepare input
        ComplexCollectionPacket input = new()
        {
            IntList = [1, 2, 3],
            StringLongDict = new Dictionary<string, long> { ["a"] = 100L, ["b"] = 200L },
            StringQueue = new Queue<string>(["q1", "q2"]),
            FloatSet = [1.1f, 2.2f],
            Tuple3 = (42, "hello", true),
            SequenceId = 1234
        };

        // 2. Measure and Serialize
        int reportedLength = input.Length;
        byte[] serialized = input.Serialize();

        // 3. Validate
        Assert.Equal(serialized.Length, reportedLength);
        Assert.True(reportedLength > PacketConstants.HeaderSize, "Packet should contain payload data.");

        // 4. Round-trip via Deserialize
        ComplexCollectionPacket output = ComplexCollectionPacket.Deserialize(serialized);

        Assert.Equal(input.Header.SequenceId, output.Header.SequenceId);
        Assert.Equal(input.IntList, output.IntList);
        Assert.Equal(input.StringLongDict, output.StringLongDict);
        Assert.Equal(input.FloatSet, output.FloatSet);
        Assert.Equal(input.Tuple3, output.Tuple3);

        // Queue validation (order is preserved by List-based serialization of collections usually)
        Assert.Equal(input.StringQueue.Count, output.StringQueue?.Count);
        while (input.StringQueue.Count > 0)
        {
            Assert.Equal(input.StringQueue.Dequeue(), output.StringQueue?.Dequeue());
        }
    }

    [Fact]
    public void ResetForPool_WhenCollectionsArePopulated_ClearsComplexCollections()
    {
        ComplexCollectionPacket packet = new()
        {
            IntList = [1, 2, 3],
            StringLongDict = new Dictionary<string, long> { ["a"] = 1 },
            StringQueue = new Queue<string>(["q1"]),
            FloatSet = [1.1f],
            Tuple3 = (1, "s", true)
        };

        packet.ResetForPool();

        Assert.NotNull(packet.IntList);
        Assert.Empty(packet.IntList);
        Assert.NotNull(packet.StringLongDict);
        Assert.Empty(packet.StringLongDict);
        Assert.NotNull(packet.StringQueue);
        Assert.Empty(packet.StringQueue);
        Assert.NotNull(packet.FloatSet);
        Assert.Empty(packet.FloatSet);
        Assert.Equal(default((int, string, bool)), packet.Tuple3);
    }

    [Fact]
    public void Serialization_WithNullVsEmptyCollections_RoundTripsDistinctly()
    {
        // Case: Explicit Null
        ComplexCollectionPacket nullPacket = new()
        {
            IntList = null,
            StringLongDict = null
        };
        ComplexCollectionPacket outputNull = ComplexCollectionPacket.Deserialize(nullPacket.Serialize());
        Assert.Null(outputNull.IntList);
        Assert.Null(outputNull.StringLongDict);

        // Case: Empty collections
        ComplexCollectionPacket emptyPacket = new()
        {
            IntList = [],
            StringLongDict = []
        };
        ComplexCollectionPacket outputEmpty = ComplexCollectionPacket.Deserialize(emptyPacket.Serialize());
        Assert.NotNull(outputEmpty.IntList);
        Assert.Empty(outputEmpty.IntList);
        Assert.NotNull(outputEmpty.StringLongDict);
        Assert.Empty(outputEmpty.StringLongDict);
    }

    [Fact]
    public void Serialization_WithUnicodeAndLargeData_RoundTripsState()
    {
        string unicodeKey = "🚀_🚀_\u4f60\u597d_TiếngViệt";
        ComplexCollectionPacket packet = new()
        {
            StringLongDict = new Dictionary<string, long>()
        };

        // Add 1000 items to stress the writer
        for (int i = 0; i < 1000; i++)
        {
            packet.StringLongDict[$"{unicodeKey}_{i}"] = i;
        }

        ComplexCollectionPacket output = ComplexCollectionPacket.Deserialize(packet.Serialize());
        Assert.Equal(1000, output.StringLongDict!.Count);
        Assert.Equal(999, output.StringLongDict[$"{unicodeKey}_999"]);
    }

    [Fact]
    public void Serialization_WithNestedPackets_RoundTripsGraph()
    {
        GraphPacket root = new()
        {
            Name = "Root",
            Nodes =
            [
                new GraphPacket { Name = "Child1", Meta = new NodeMeta { Id = 101 } },
                new GraphPacket { Name = "Child2", Nodes = [ new GraphPacket { Name = "GrandChild" } ] }
            ]
        };

        byte[] serialized = root.Serialize();
        Assert.True(root.Length <= serialized.Length);

        GraphPacket output = GraphPacket.Deserialize(serialized);

        Assert.Equal("Root", output.Name);
        Assert.Equal(2, output.Nodes!.Count);
        Assert.Equal(101, output.Nodes[0].Meta.Id);
        Assert.Equal("GrandChild", output.Nodes[1].Nodes![0].Name);
    }

    [Fact]
    public void Serialization_WithNestedClassContainingCollections_RoundTripsState()
    {
        NestedCollectionPacket packet = new()
        {
            User = new UserDetails
            {
                Username = "nalix_dev",
                Roles = ["admin", "tester", "developer"],
                Attributes = new Dictionary<string, string>
                {
                    ["region"] = "asia",
                    ["tier"] = "premium"
                }
            }
        };

        NestedCollectionPacket output = NestedCollectionPacket.Deserialize(packet.Serialize());

        Assert.NotNull(output.User);
        Assert.Equal("nalix_dev", output.User.Username);
        Assert.Equal(["admin", "tester", "developer"], output.User.Roles);
        Assert.Equal("premium", output.User.Attributes["tier"]);
    }

    [Fact]
    public void Serialization_WithDeeplyNestedCollections_RoundTrips()
    {
        ExtremeNestedPacket packet = new()
        {
            Data = new List<Dictionary<string, List<int>>>
            {
                new() { ["a"] = [1, 2], ["b"] = [3] },
                new() { ["c"] = [] },
                new() { ["d"] = [4, 5, 6, 7] }
            }
        };

        byte[] serialized = packet.Serialize();
        Assert.Equal(serialized.Length, packet.Length); // Strict equality check

        ExtremeNestedPacket output = ExtremeNestedPacket.Deserialize(serialized);

        Assert.Equal(3, output.Data!.Count);
        Assert.Equal([1, 2], output.Data[0]["a"]);
        Assert.Empty(output.Data[1]["c"]);
        Assert.Equal([4, 5, 6, 7], output.Data[2]["d"]);
    }

    [Fact]
    public void Serialization_WithLargePayload_ExpandsWriterBuffer()
    {
        // Create a large packet (>100KB) to force DataWriter expansion
        LargeDataPacket packet = new()
        {
            Payload = [.. Enumerable.Range(0, 30000).Select(i => $"String_Data_Index_{i}")]
        };

        byte[] bytes = packet.Serialize();
        Assert.True(bytes.Length > 100 * 1024, "Packet should be larger than 100KB.");
        Assert.Equal(bytes.Length, packet.Length); // Strict equality check

        LargeDataPacket output = LargeDataPacket.Deserialize(bytes);
        Assert.Equal(30000, output.Payload!.Count);
        Assert.Equal("String_Data_Index_29999", output.Payload[29999]);
    }

    [Fact]
    public void Serialization_WithNullAndWhitespaceStrings_PreservesEachElement()
    {
        NullStressPacket packet = new()
        {
            Items = ["", " ", null!, "\t", "\n", "content"]
        };

        byte[] serialized = packet.Serialize();
        Assert.Equal(serialized.Length, packet.Length); // Strict equality check

        NullStressPacket output = NullStressPacket.Deserialize(serialized);

        Assert.Equal("", output.Items![0]);
        Assert.Equal(" ", output.Items[1]);
        Assert.Null(output.Items[2]);
        Assert.Equal("\t", output.Items[3]);
        Assert.Equal("\n", output.Items[4]);
        Assert.Equal("content", output.Items[5]);
    }

    [Fact]
    public void Serialization_WithSpecialFloatingPointValues_RoundTrips()
    {
        FloatStressPacket packet = new()
        {
            Values = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, float.MaxValue, float.MinValue]
        };

        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, packet.Length);

        FloatStressPacket output = FloatStressPacket.Deserialize(bytes);
        Assert.True(float.IsNaN(output.Values![0]));
        Assert.True(float.IsPositiveInfinity(output.Values[1]));
        Assert.True(float.IsNegativeInfinity(output.Values[2]));
        Assert.Equal(float.Epsilon, output.Values[3]);
        Assert.Equal(float.MaxValue, output.Values[4]);
        Assert.Equal(float.MinValue, output.Values[5]);
    }

    [Fact]
    public void Serialization_WithDeeplyNestedLists_RoundTrips()
    {
        DeepListPacket packet = new()
        {
            Matrix =
            [
                [ ["a", "b"], ["c"] ],
                [],
                [ [], ["d"] ]
            ]
        };

        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, packet.Length);

        DeepListPacket output = DeepListPacket.Deserialize(bytes);
        Assert.Equal(3, output.Matrix!.Count);
        Assert.Equal("b", output.Matrix[0][0][1]);
        Assert.Empty(output.Matrix[1]);
        Assert.Equal("d", output.Matrix[2][1][0]);
    }

    [Fact]
    public void Serialization_WithEnumCollections_RoundTrips()
    {
        EnumListPacket packet = new()
        {
            Priorities = [PacketPriority.URGENT, PacketPriority.LOW, PacketPriority.HIGH]
        };

        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, packet.Length);

        EnumListPacket output = EnumListPacket.Deserialize(bytes);
        Assert.Equal(new[] { PacketPriority.URGENT, PacketPriority.LOW, PacketPriority.HIGH }, output.Priorities);
    }

    [Fact]
    public void Deserialize_WhenBufferIsTruncated_ReturnsNull()
    {
        LargeDataPacket packet = new() { Payload = ["valid", "test"] };
        byte[] validBytes = packet.Serialize();

        // Corrupt the length of a string in the middle of the payload
        // This should cause the Zero-Throw pipeline to return null instead of crashing
        byte[] corrupted = [.. validBytes.Take(validBytes.Length - 5)];

        Assert.Null(LargeDataPacket.Deserialize(corrupted));
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class ExtremeNestedPacket : PacketBase<ExtremeNestedPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public List<Dictionary<string, List<int>>>? Data { get; set; }
        public static new ExtremeNestedPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<ExtremeNestedPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class LargeDataPacket : PacketBase<LargeDataPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public List<string>? Payload { get; set; }
        public static new LargeDataPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<LargeDataPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class NullStressPacket : PacketBase<NullStressPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public List<string>? Items { get; set; }
        public static new NullStressPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<NullStressPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class NestedCollectionPacket : PacketBase<NestedCollectionPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public UserDetails? User { get; set; }

        public static new NestedCollectionPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<NestedCollectionPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    public sealed class UserDetails
    {
        public string Username { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = [];
        public Dictionary<string, string> Attributes { get; set; } = [];

        public static UserDetails Create() => new();
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class GraphPacket : PacketBase<GraphPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public string Name { get; set; } = string.Empty;
        public List<GraphPacket>? Nodes { get; set; }
        public NodeMeta Meta { get; set; }

        public static new GraphPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<GraphPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    public struct NodeMeta
    {
        public int Id { get; set; }
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class ComplexCollectionPacket : PacketBase<ComplexCollectionPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        [SerializeOrder(0)]
        public List<int>? IntList { get; set; }

        [SerializeOrder(1)]
        public Dictionary<string, long>? StringLongDict { get; set; }

        [SerializeOrder(2)]
        public Queue<string>? StringQueue { get; set; }

        [SerializeOrder(3)]
        public HashSet<float>? FloatSet { get; set; }

        [SerializeOrder(4)]
        public (int Id, string Name, bool Active) Tuple3 { get; set; }

        public static new ComplexCollectionPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<ComplexCollectionPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class FloatStressPacket : PacketBase<FloatStressPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public List<float>? Values { get; set; }
        public static new FloatStressPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<FloatStressPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class ObjectListPacket : PacketBase<ObjectListPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public List<UserDetails?>? Users { get; set; }
        public static new ObjectListPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<ObjectListPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class DeepListPacket : PacketBase<DeepListPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public List<List<List<string>>>? Matrix { get; set; }
        public static new DeepListPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<DeepListPacket>.Deserialize(buffer);
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    public sealed partial class EnumListPacket : PacketBase<EnumListPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public List<PacketPriority>? Priorities { get; set; }
        public static new EnumListPacket Deserialize(ReadOnlySpan<byte> buffer)
            => PacketBase<EnumListPacket>.Deserialize(buffer);
    }
}




















