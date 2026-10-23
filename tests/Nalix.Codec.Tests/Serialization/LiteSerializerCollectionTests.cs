// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.Serialization;
using Nalix.Codec.Tests.TestSupport;

namespace Nalix.Codec.Tests.Serialization;

public sealed class LiteSerializerCollectionTests
{
    [Fact]
    public void SerializeDeserialize_List_RoundTripsState()
    {
        List<int> input = [10, 20, 30, 40, 50];
        List<int>? output = null;
        _ = LiteSerializerRoundTrip.RoundTrip(input, ref output);

        Assert.NotNull(output);
        Assert.Equal(input, output);
    }

    [Fact]
    public void SerializeDeserialize_Dictionary_RoundTripsState()
    {
        Dictionary<string, int> input = new()
        {
            ["one"] = 1,
            ["two"] = 2,
            ["three"] = 3
        };
        Dictionary<string, int>? output = null;
        _ = LiteSerializerRoundTrip.RoundTrip(input, ref output);

        Assert.NotNull(output);
        Assert.Equal(input.Count, output.Count);
        foreach (KeyValuePair<string, int> kvp in input)
        {
            Assert.True(output.TryGetValue(kvp.Key, out int val));
            Assert.Equal(kvp.Value, val);
        }
    }

    [Fact]
    public void SerializeDeserialize_Queue_RoundTripsState()
    {
        Queue<string> input = new();
        input.Enqueue("first");
        input.Enqueue("second");
        input.Enqueue("third");

        Queue<string>? output = null;
        _ = LiteSerializerRoundTrip.RoundTrip(input, ref output);

        Assert.NotNull(output);
        Assert.Equal(input.Count, output.Count);
        Assert.Equal("first", output.Dequeue());
        Assert.Equal("second", output.Dequeue());
        Assert.Equal("third", output.Dequeue());
    }

    [Fact]
    public void SerializeDeserialize_Stack_RoundTripsState()
    {
        Stack<float> input = new();
        input.Push(1.1f);
        input.Push(2.2f);
        input.Push(3.3f);

        Stack<float>? output = null;
        _ = LiteSerializerRoundTrip.RoundTrip(input, ref output);

        Assert.NotNull(output);
        Assert.Equal(input.Count, output.Count);
        Assert.Equal(3.3f, output.Pop());
        Assert.Equal(2.2f, output.Pop());
        Assert.Equal(1.1f, output.Pop());
    }

    [Fact]
    public void SerializeDeserialize_HashSet_RoundTripsState()
    {
        HashSet<int> input = [1, 2, 3, 4, 5];
        HashSet<int>? output = null;
        _ = LiteSerializerRoundTrip.RoundTrip(input, ref output);

        Assert.NotNull(output);
        Assert.Equal(input.Count, output.Count);
        Assert.Subset(input, output);
    }

    [Fact]
    public void SerializeDeserialize_Memory_RoundTripsState()
    {
        Memory<int> input = new int[] { 100, 200, 300, 400 };
        Memory<int> output = default;
        _ = LiteSerializerRoundTrip.RoundTrip(input, ref output);

        Assert.Equal(input.Length, output.Length);
        Assert.Equal(input.ToArray(), output.ToArray());
    }

    [Fact]
    public void SerializeDeserialize_ReadOnlyMemory_RoundTripsState()
    {
        ReadOnlyMemory<long> input = new long[] { 1L, 2L, 3L };
        ReadOnlyMemory<long> output = default;
        _ = LiteSerializerRoundTrip.RoundTrip(input, ref output);

        Assert.Equal(input.Length, output.Length);
        Assert.Equal(input.ToArray(), output.ToArray());
    }

    [Fact]
    public void Deserialize_WhenMemoryLengthIsTooLarge_ThrowsSerializationFailureException()
    {
        byte[] buffer = BitConverter.GetBytes(1_048_577);
        Memory<int> output = default;

        _ = Assert.ThrowsAny<Abstractions.Exceptions.SerializationFailureException>(() => LiteSerializer.Deserialize(buffer, ref output));
    }

    [Fact]
    public void Deserialize_WhenListCountExceedsPayload_ThrowsSerializationFailureException()
    {
        byte[] buffer = BitConverter.GetBytes(1_048_576);
        List<string>? output = null;

        _ = Assert.ThrowsAny<Abstractions.Exceptions.SerializationFailureException>(() => LiteSerializer.Deserialize(buffer, ref output));
        Assert.Null(output);
    }

    [Fact]
    public void Deserialize_WhenDictionaryCountExceedsPayload_ThrowsSerializationFailureException()
    {
        byte[] buffer = BitConverter.GetBytes(1_048_576);
        Dictionary<string, int>? output = null;

        _ = Assert.ThrowsAny<Abstractions.Exceptions.SerializationFailureException>(() => LiteSerializer.Deserialize(buffer, ref output));
        Assert.Null(output);
    }

    [Fact]
    public void SerializeDeserialize_WhenValueTuplesHaveArityTwoToFive_RoundTripsState()
    {
        // Arity 2
        (int, string) t2 = (1, "two");
        (int, string) r2 = LiteSerializerRoundTrip.RoundTrip(t2);
        Assert.Equal(t2, r2);

        // Arity 3
        (int, string, float) t3 = (1, "two", 3.3f);
        (int, string, float) r3 = LiteSerializerRoundTrip.RoundTrip(t3);
        Assert.Equal(t3, r3);

        // Arity 4
        (int, string, float, long) t4 = (1, "two", 3.3f, 4L);
        (int, string, float, long) r4 = LiteSerializerRoundTrip.RoundTrip(t4);
        Assert.Equal(t4, r4);

        // Arity 5
        (int, string, float, long, bool) t5 = (1, "two", 3.3f, 4L, true);
        (int, string, float, long, bool) r5 = LiteSerializerRoundTrip.RoundTrip(t5);
        Assert.Equal(t5, r5);
    }

    [Fact]
    public void SerializeDeserialize_UnmarkedComplexClass_ThrowsSerializationFailureException()
    {
        AutoTestClass input = new()
        {
            IntVal = 123,
            StringVal = "AutoTest",
            Nested = new AutoTestStruct { FloatVal = 4.5f, LongVal = 678L }
        };

        TypeInitializationException ex = Assert.Throws<TypeInitializationException>(
            () => LiteSerializer.Serialize(in input));

        Abstractions.Exceptions.SerializationFailureException inner =
            Assert.IsType<Abstractions.Exceptions.SerializationFailureException>(ex.InnerException);
        Assert.Contains("No formatter registered", inner.Message, StringComparison.Ordinal);
        Assert.Contains("[GenerateFormatter]", inner.Message, StringComparison.Ordinal);
    }

    private sealed class AutoTestClass
    {
        public int IntVal { get; set; }
        public string StringVal { get; set; } = string.Empty;
        public AutoTestStruct Nested { get; set; }
    }

    private struct AutoTestStruct
    {
        public float FloatVal { get; set; }
        public long LongVal { get; set; }
    }
}















