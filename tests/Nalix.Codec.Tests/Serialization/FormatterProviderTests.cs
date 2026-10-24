// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Serialization;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Serialization;

public sealed class FormatterProviderTests
{
    [Fact]
    public void Get_WhenCalledRepeatedly_ReturnsSameCachedInstance()
    {
        IFormatter<int> first = FormatterProvider.Get<int>();
        IFormatter<int> second = FormatterProvider.Get<int>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Get_WhenCommonCollectionAndTupleTypes_ResolvesFormatters()
    {
        IFormatter<int[]> arrayFormatter = FormatterProvider.Get<int[]>();
        IFormatter<int?[]> nullableArrayFormatter = FormatterProvider.Get<int?[]>();
        IFormatter<Queue<int>> queueFormatter = FormatterProvider.Get<Queue<int>>();
        IFormatter<Stack<int>> stackFormatter = FormatterProvider.Get<Stack<int>>();
        IFormatter<HashSet<int>> hashSetFormatter = FormatterProvider.Get<HashSet<int>>();
        IFormatter<List<int>> listFormatter = FormatterProvider.Get<List<int>>();
        IFormatter<List<int?>> nullableListFormatter = FormatterProvider.Get<List<int?>>();
        IFormatter<Dictionary<int, int>> dictFormatter = FormatterProvider.Get<Dictionary<int, int>>();
        IFormatter<Memory<int>> memoryFormatter = FormatterProvider.Get<Memory<int>>();
        IFormatter<ReadOnlyMemory<int>> readOnlyMemoryFormatter = FormatterProvider.Get<ReadOnlyMemory<int>>();
        IFormatter<(int, int)> tupleFormatter = FormatterProvider.Get<(int, int)>();
        IFormatter<(int, int, int, int, int)> tuple5Formatter = FormatterProvider.Get<(int, int, int, int, int)>();

        Assert.NotNull(arrayFormatter);
        Assert.NotNull(nullableArrayFormatter);
        Assert.NotNull(queueFormatter);
        Assert.NotNull(stackFormatter);
        Assert.NotNull(hashSetFormatter);
        Assert.NotNull(listFormatter);
        Assert.NotNull(nullableListFormatter);
        Assert.NotNull(dictFormatter);
        Assert.NotNull(memoryFormatter);
        Assert.NotNull(readOnlyMemoryFormatter);
        Assert.NotNull(tupleFormatter);
        Assert.NotNull(tuple5Formatter);
    }

    [Fact]
    public void Get_WhenMemoryElementTypeIsManaged_ThrowsSerializationFailureException() => _ = Assert.Throws<SerializationFailureException>(FormatterProvider.Get<Memory<string>>);

    [Fact]
    public void Register_WhenFormatterProvided_OverridesCachedFormatter()
    {
        StubFormatter formatter = new();

        FormatterProvider.Register<StubType>(formatter);
        IFormatter<StubType> resolved = FormatterProvider.Get<StubType>();

        DataReader reader = new([]);
        StubType value = resolved.Deserialize(ref reader);
        Assert.NotNull(value);
    }


    [Fact]
    public void RegisterComplex_WhenTypeIsUnsupported_ThrowsSerializationFailureException()
    {
        _ = Assert.Throws<SerializationFailureException>(() =>
            FormatterProvider.RegisterComplex<IUnsupported>(new UnsupportedFormatter()));
    }

    private sealed class StubType
    {
        public int Value { get; set; }
    }

    private sealed class StubFormatter : IFormatter<StubType>
    {
        public StubType Deserialize(ref DataReader reader) => new();
        public void Serialize(ref DataWriter writer, in StubType value) { }
    }


    private interface IUnsupported
    {
    }

    private sealed class UnsupportedFormatter : IFormatter<IUnsupported>
    {
        public IUnsupported Deserialize(ref DataReader reader) => throw new NotSupportedException();
        public void Serialize(ref DataWriter writer, in IUnsupported value) { }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Public API tests for AOT-safe formatter resolution
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Get_ListOfInt_ResolvesSuccessfully()
    {
        IFormatter<List<int>> formatter = FormatterProvider.Get<List<int>>();

        Assert.NotNull(formatter);

        // Verify caching
        IFormatter<List<int>> second = FormatterProvider.Get<List<int>>();
        Assert.Same(formatter, second);
    }

    [Fact]
    public void Get_DictionaryStringInt_ResolvesSuccessfully()
    {
        IFormatter<Dictionary<string, int>> formatter = FormatterProvider.Get<Dictionary<string, int>>();

        Assert.NotNull(formatter);
        Assert.Same(formatter, FormatterProvider.Get<Dictionary<string, int>>());
    }

    [Fact]
    public void Get_IntArray_ResolvesSuccessfully()
    {
        IFormatter<int[]> formatter = FormatterProvider.Get<int[]>();

        Assert.NotNull(formatter);
        Assert.Same(formatter, FormatterProvider.Get<int[]>());
    }

    [Fact]
    public void Get_NullableInt_ResolvesSuccessfully()
    {
        IFormatter<int?> formatter = FormatterProvider.Get<int?>();

        Assert.NotNull(formatter);

        // Round-trip via LiteSerializer
        int? value = 42;
        byte[] bytes = LiteSerializer.Serialize(value);
        int? result = LiteSerializer.Deserialize<int?>(bytes, out _);
        Assert.Equal(42, result);

        // Null round-trip
        int? nullValue = null;
        byte[] nullBytes = LiteSerializer.Serialize(nullValue);
        int? nullResult = LiteSerializer.Deserialize<int?>(nullBytes, out _);
        Assert.Null(nullResult);
    }

    [Fact]
    public void Get_QueueOfString_ResolvesSuccessfully()
    {
        IFormatter<Queue<string>> formatter = FormatterProvider.Get<Queue<string>>();

        Assert.NotNull(formatter);
        Assert.Same(formatter, FormatterProvider.Get<Queue<string>>());
    }

    [Fact]
    public void Get_ValueTuple3_ResolvesAndRoundTrips()
    {
        IFormatter<(int, string, bool)> formatter = FormatterProvider.Get<(int, string, bool)>();

        Assert.NotNull(formatter);

        (int, string, bool) value = (42, "hello", true);
        byte[] bytes = LiteSerializer.Serialize(value);
        (int, string, bool) result = LiteSerializer.Deserialize<(int, string, bool)>(bytes, out _);

        Assert.Equal(value, result);
    }

    [Fact]
    public void Get_UnregisteredComplexType_ThrowsSerializationFailureException()
    {
        Assert.Throws<SerializationFailureException>(FormatterProvider.Get<UnregisteredDto>);
    }

    [Fact]
    public void RegisterGenerated_ThenGet_ReturnsSameInstance()
    {
        // Use a unique type to avoid interference with other tests
        FormatterProvider.RegisterGenerated<RegisterTestDto>(new RegisterTestDtoFormatter());

        IFormatter<RegisterTestDto> resolved = FormatterProvider.Get<RegisterTestDto>();

        Assert.NotNull(resolved);
        Assert.IsType<RegisterTestDtoFormatter>(resolved);
    }

    private sealed class UnregisteredDto
    {
        public int Id { get; set; }
    }

    private sealed class RegisterTestDto
    {
        public string Name { get; set; } = "";
    }

    private sealed class RegisterTestDtoFormatter : IFormatter<RegisterTestDto>
    {
        public RegisterTestDto Deserialize(ref DataReader reader) => new();
        public void Serialize(ref DataWriter writer, in RegisterTestDto value) { }
    }
}
















