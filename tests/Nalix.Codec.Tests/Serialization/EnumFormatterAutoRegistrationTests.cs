// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.Serialization;
using Nalix.Codec.Tests.TestSupport;

namespace Nalix.Codec.Tests.Serialization;

/// <summary>
/// Verifies that <see cref="SerializeFormatterGenerator"/> automatically emits
/// <c>FormatterProvider.RegisterAllFormatters&lt;TEnum&gt;()</c> for every enum type
/// used inside <c>[GenerateFormatter]</c> DTO fields — including inside
/// List, Dictionary, Array, Nullable, Queue, Stack, HashSet, ValueTuple,
/// Memory, and ReadOnlyMemory wrappers.
/// </summary>
public sealed class EnumFormatterAutoRegistrationTests
{
    // ══════════════════════════════════════════════════════════════════════
    //  Test enum used by the DTO below.
    //  No manual RegisterAllFormatters<TestStatus>() call is made — the
    //  source generator must emit it automatically.
    // ══════════════════════════════════════════════════════════════════════

    internal enum TestStatus : byte
    {
        None = 0,
        Active = 1,
        Inactive = 2,
        Archived = 3
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DTO exercising every supported enum wrapper shape.
    // ══════════════════════════════════════════════════════════════════════

    [GenerateFormatter]
    internal sealed partial class EnumWrapperDto
    {
        [SerializeOrder(0)]
        internal TestStatus Direct { get; set; }

        [SerializeOrder(1)]
        internal TestStatus? Nullable { get; set; }

        [SerializeOrder(2)]
        internal TestStatus[]? Array { get; set; }

        [SerializeOrder(3)]
        internal List<TestStatus>? List { get; set; }

        [SerializeOrder(4)]
        internal Dictionary<string, TestStatus>? Dictionary { get; set; }

        [SerializeOrder(5)]
        internal Queue<TestStatus>? Queue { get; set; }

        [SerializeOrder(6)]
        internal Stack<TestStatus>? Stack { get; set; }

        [SerializeOrder(7)]
        internal HashSet<TestStatus>? HashSet { get; set; }

        [SerializeOrder(8)]
        internal (TestStatus First, string Label, TestStatus? Last) Tuple { get; set; }

        internal static EnumWrapperDto Create() => new();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Tests: DTO round-trip (no manual RegisterAllFormatters)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Dto_WithAllEnumWrapperShapes_RoundTripsCorrectly()
    {
        EnumWrapperDto original = new()
        {
            Direct = TestStatus.Active,
            Nullable = TestStatus.Inactive,
            Array = [TestStatus.Active, TestStatus.None, TestStatus.Archived],
            List = [TestStatus.Inactive, TestStatus.Active],
            Dictionary = new Dictionary<string, TestStatus>
            {
                ["a"] = TestStatus.Active,
                ["b"] = TestStatus.Archived
            },
            Queue = new Queue<TestStatus>([TestStatus.None, TestStatus.Active]),
            Stack = new Stack<TestStatus>([TestStatus.Archived, TestStatus.Inactive]),
            HashSet = [TestStatus.Active, TestStatus.Inactive],
            Tuple = (TestStatus.Active, "label", TestStatus.Archived)
        };

        byte[] bytes = LiteSerializer.Serialize(original);

        EnumWrapperDto deserialized = LiteSerializerRoundTrip.RoundTrip(original);

        deserialized.Direct.Should().Be(TestStatus.Active);
        deserialized.Nullable.Should().Be(TestStatus.Inactive);
        deserialized.Array.Should().Equal(TestStatus.Active, TestStatus.None, TestStatus.Archived);
        deserialized.List.Should().Equal(TestStatus.Inactive, TestStatus.Active);
        deserialized.Dictionary.Should().ContainKey("a").WhoseValue.Should().Be(TestStatus.Active);
        deserialized.Dictionary.Should().ContainKey("b").WhoseValue.Should().Be(TestStatus.Archived);
        deserialized.Queue.Should().Equal(TestStatus.None, TestStatus.Active);
        deserialized.Stack.Should().Equal(TestStatus.Inactive, TestStatus.Archived); // Stack reverses LIFO
        deserialized.HashSet.Should().BeEquivalentTo([TestStatus.Active, TestStatus.Inactive]);
        deserialized.Tuple.First.Should().Be(TestStatus.Active);
        deserialized.Tuple.Label.Should().Be("label");
        deserialized.Tuple.Last.Should().Be(TestStatus.Archived);
    }

    [Fact]
    public void Dto_WithNullEnumCollections_RoundTripsNulls()
    {
        EnumWrapperDto original = new()
        {
            Direct = TestStatus.None,
            Nullable = null,
            Array = null,
            List = null,
            Dictionary = null,
            Queue = null,
            Stack = null,
            HashSet = null,
            Tuple = (TestStatus.None, "", null)
        };

        EnumWrapperDto deserialized = LiteSerializerRoundTrip.RoundTrip(original);

        deserialized.Direct.Should().Be(TestStatus.None);
        deserialized.Nullable.Should().BeNull();
        deserialized.Array.Should().BeNull();
        deserialized.List.Should().BeNull();
        deserialized.Dictionary.Should().BeNull();
        deserialized.Queue.Should().BeNull();
        deserialized.Stack.Should().BeNull();
        deserialized.HashSet.Should().BeNull();
        deserialized.Tuple.Last.Should().BeNull();
    }

    [Fact]
    public void Dto_WithEmptyEnumCollections_RoundTripsEmpty()
    {
        EnumWrapperDto original = new()
        {
            Direct = TestStatus.Active,
            Array = [],
            List = [],
            Dictionary = [],
            Queue = new Queue<TestStatus>(),
            Stack = new Stack<TestStatus>(),
            HashSet = [],
            Tuple = (TestStatus.None, "empty", null)
        };

        EnumWrapperDto deserialized = LiteSerializerRoundTrip.RoundTrip(original);

        deserialized.Array.Should().BeEmpty();
        deserialized.List.Should().BeEmpty();
        deserialized.Dictionary.Should().BeEmpty();
        deserialized.Queue.Should().BeEmpty();
        deserialized.Stack.Should().BeEmpty();
        deserialized.HashSet.Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Tests: Direct public API resolution (after module initializer runs)
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Get_WhenListOfEnum_ResolvesWithoutManualRegistration()
    {
        IFormatter<List<TestStatus>> formatter = null!;
        Action act = () => formatter = FormatterProvider.Get<List<TestStatus>>();

        act.Should().NotThrow();
        formatter.Should().NotBeNull();
    }

    [Fact]
    public void Get_WhenDictionaryOfEnum_ResolvesWithoutManualRegistration()
    {
        IFormatter<Dictionary<string, TestStatus>> formatter = null!;
        Action act = () => formatter = FormatterProvider.Get<Dictionary<string, TestStatus>>();

        act.Should().NotThrow();
        formatter.Should().NotBeNull();
    }

    [Fact]
    public void Get_WhenEnumArray_ResolvesWithoutManualRegistration()
    {
        IFormatter<TestStatus[]> formatter = null!;
        Action act = () => formatter = FormatterProvider.Get<TestStatus[]>();

        act.Should().NotThrow();
        formatter.Should().NotBeNull();
    }

    [Fact]
    public void Get_WhenNullableEnum_ResolvesWithoutManualRegistration()
    {
        IFormatter<TestStatus?> formatter = null!;
        Action act = () => formatter = FormatterProvider.Get<TestStatus?>();

        act.Should().NotThrow();
        formatter.Should().NotBeNull();
    }

    [Fact]
    public void Get_WhenQueueOfEnum_ResolvesWithoutManualRegistration()
    {
        IFormatter<Queue<TestStatus>> formatter = null!;
        Action act = () => formatter = FormatterProvider.Get<Queue<TestStatus>>();

        act.Should().NotThrow();
        formatter.Should().NotBeNull();
    }

    [Fact]
    public void Get_WhenStackOfEnum_ResolvesWithoutManualRegistration()
    {
        IFormatter<Stack<TestStatus>> formatter = null!;
        Action act = () => formatter = FormatterProvider.Get<Stack<TestStatus>>();

        act.Should().NotThrow();
        formatter.Should().NotBeNull();
    }

    [Fact]
    public void Get_WhenHashSetOfEnum_ResolvesWithoutManualRegistration()
    {
        IFormatter<HashSet<TestStatus>> formatter = null!;
        Action act = () => formatter = FormatterProvider.Get<HashSet<TestStatus>>();

        act.Should().NotThrow();
        formatter.Should().NotBeNull();
    }
}

