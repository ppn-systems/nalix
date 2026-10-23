// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Serialization;
using Nalix.Codec.Tests.TestSupport;

namespace Nalix.Codec.Tests.Serialization;

public sealed class LiteSerializerUnmanagedTests
{
    [Fact]
    public void SerializeDeserialize_UnmanagedStruct_RoundTripsValue()
    {
        ComplexStruct input = new() { I32 = 123456789, I16 = -1234, B = 0xAB };
        byte[] data = LiteSerializer.Serialize(in input);

        ComplexStruct output = default;
        int read = LiteSerializer.Deserialize(data, ref output);

        Assert.Equal(data.Length, read);
        Assert.Equal(input.I32, output.I32);
        Assert.Equal(input.I16, output.I16);
        Assert.Equal(input.B, output.B);
    }

    [Fact]
    public void SerializeDeserialize_UnmarkedClass_ThrowsSerializationFailureException()
    {
        NullClass input = new() { I32 = [], I16 = null };

        TypeInitializationException ex = Assert.Throws<TypeInitializationException>(
            () => LiteSerializer.Serialize(in input));

        SerializationFailureException inner = Assert.IsType<SerializationFailureException>(ex.InnerException);
        Assert.Contains("No formatter registered", inner.Message, StringComparison.Ordinal);
        Assert.Contains("[GenerateFormatter]", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WhenBufferHasExactSize_WritesUnmanagedStruct()
    {
        ComplexStruct value = new() { I32 = 42, I16 = 7, B = 1 };
        byte[] exact = LiteSerializer.Serialize(in value);
        byte[] buffer = new byte[exact.Length];

        int written = LiteSerializer.Serialize(in value, buffer);
        ComplexStruct output = default;
        int read = LiteSerializer.Deserialize(buffer, ref output);

        Assert.Equal(exact.Length, written);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(value.I32, output.I32);
        Assert.Equal(value.I16, output.I16);
        Assert.Equal(value.B, output.B);
    }

    [Fact]
    public void Serialize_WhenBufferIsTooShort_ThrowsSerializationFailureException()
    {
        ComplexStruct value = new() { I32 = 1, I16 = 2, B = 3 };
        byte[] exact = LiteSerializer.Serialize(in value);
        byte[] tooSmall = new byte[exact.Length - 1];

        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Serialize(in value, tooSmall));
    }

    [Fact]
    public void Deserialize_WhenUnmanagedBufferIsTooShort_ThrowsSerializationFailureException()
    {
        ComplexStruct value = new() { I32 = 1, I16 = 2, B = 3 };
        byte[] full = LiteSerializer.Serialize(in value);
        byte[] shortBuffer = new byte[full.Length - 1];
        Array.Copy(full, shortBuffer, shortBuffer.Length);

        ComplexStruct destination = default;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(shortBuffer, ref destination));
    }
}
















