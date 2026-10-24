// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Codec.Tests.DataFrames;

public sealed partial class PacketBaseLengthTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("xin chao")]
    [InlineData("Xin chao Viet Nam")]
    [InlineData("xin chao \u4f60\u597d")]
    public void Length_WhenPacketContainsString_MatchesSerializedByteCount(string text)
    {
        StringPacket packet = new()
        {
            Message = text,
            Header = new PacketHeader { SequenceId = 42 }
        };

        byte[] bytes = packet.Serialize();

        Debug.WriteLine("text: " + text + "| bytes: " + Encoding.UTF8.GetBytes(text).Length);
        Debug.WriteLine("bytes.Length: " + bytes.Length);
        Debug.WriteLine("packet.Length: " + packet.Length);
        Assert.Equal(bytes.Length, packet.Length);

        byte[] buffer = new byte[packet.Length];
        int written = packet.Serialize(buffer);

        Assert.Equal(packet.Length, written);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void Length_WhenPacketContainsNestedPacket_MatchesSerializedByteCount()
    {
        ParentPacket packet = new()
        {
            Child = new ChildPacket
            {
                Value = 123456789,
                Header = new PacketHeader { SequenceId = 7 }
            }
        };

        byte[] bytes = packet.Serialize();

        Assert.Equal(bytes.Length, packet.Length);

        byte[] buffer = new byte[packet.Length];
        int written = packet.Serialize(buffer);

        Assert.Equal(packet.Length, written);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void Serialize_WhenBufferIsSmallerThanDynamicLength_ThrowsArgumentException()
    {
        StringPacket packet = new()
        {
            Message = "dynamic payload"
        };

        byte[] buffer = new byte[packet.Length - 1];
        ArgumentException ex = Assert.Throws<ArgumentException>(() => packet.Serialize(buffer));

        Assert.Contains("Buffer too small", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(packet.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Length_WhenStringUsesDynamicSizeAttribute_MatchesSerializedByteCount()
    {
        DynamicHintStringPacket packet = new()
        {
            Message = "xin chao"
        };

        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, packet.Length);

        byte[] buffer = new byte[packet.Length];
        int written = packet.Serialize(buffer);
        Assert.Equal(packet.Length, written);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void Length_WhenPacketContainsList_MatchesSerializedByteCount()
    {
        ListPacket packet = new()
        {
            Values = [1, 2, 3, 4, 5]
        };

        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, packet.Length);

        byte[] buffer = new byte[packet.Length];
        int written = packet.Serialize(buffer);
        Assert.Equal(packet.Length, written);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void Length_WhenPacketContainsDictionary_MatchesSerializedByteCount()
    {
        DictionaryPacket packet = new()
        {
            Values = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["alpha"] = 1,
                ["beta"] = 2,
                ["xin chao"] = 3
            }
        };

        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, packet.Length);

        byte[] buffer = new byte[packet.Length];
        int written = packet.Serialize(buffer);
        Assert.Equal(packet.Length, written);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void Length_WhenPacketContainsNullDictionary_MatchesSerializedByteCount()
    {
        DictionaryPacket packet = new()
        {
            Values = null
        };

        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, packet.Length);

        byte[] buffer = new byte[packet.Length];
        int written = packet.Serialize(buffer);
        Assert.Equal(packet.Length, written);
        Assert.Equal(bytes, buffer);
    }

    [Fact]
    public void Length_WhenPacketContainsLargeStringDictionary_MatchesSerializedByteCount()
    {
        StringDictionaryPacket packet = new()
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        for (int i = 0; i < 80; i++)
        {
            packet.Values["Field" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)] =
                new string((char)('a' + (i % 26)), 512);
        }

        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, packet.Length);

        byte[] buffer = new byte[packet.Length];
        int written = packet.Serialize(buffer);
        Assert.Equal(packet.Length, written);
        Assert.Equal(bytes, buffer);
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    internal sealed partial class StringPacket : PacketBase<StringPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        [SerializeOrder(0)]
        public string Message { get; set; } = string.Empty;
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    internal sealed partial class ChildPacket : PacketBase<ChildPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        [SerializeOrder(0)]
        public int Value { get; set; }
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    internal sealed partial class ParentPacket : PacketBase<ParentPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public ChildPacket? Child { get; set; }
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    internal sealed partial class ListPacket : PacketBase<ListPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public List<int>? Values { get; set; }
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    internal sealed partial class DictionaryPacket : PacketBase<DictionaryPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public Dictionary<string, int>? Values { get; set; }
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    internal sealed partial class StringDictionaryPacket : PacketBase<StringDictionaryPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        public Dictionary<string, string>? Values { get; set; }
    }

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Sequential)]
    internal sealed partial class DynamicHintStringPacket : PacketBase<DynamicHintStringPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 9999;
        [SerializeDynamicSize(64)]
        public string Message { get; set; } = string.Empty;
    }
}














