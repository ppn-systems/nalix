// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.Tests.TestSupport;

namespace Nalix.Codec.Tests.Serialization;

public sealed class LiteSerializerObjectTests
{

    [Fact]
    public void SerializeDeserialize_Object_RoundTripsState()
    {
        TestObject input = new() { Id = 7, Name = "Alice" };
        TestObject? output = null;
        _ = LiteSerializerRoundTrip.RoundTrip(input, ref output);

        Assert.NotNull(output);
        Assert.Equal(input.Id, output.Id);
        Assert.Equal(input.Name, output.Name);
    }

    [Fact]
    public void SerializeDeserialize_ValueStruct_RoundTripsState()
    {
        TestStruct input = new(42, 3.14f);
        TestStruct output = LiteSerializerRoundTrip.RoundTrip(input);

        Assert.Equal(input.X, output.X);
        Assert.Equal(input.Y, output.Y, precision: 3);
    }
}















