// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.Tests.TestSupport;

namespace Nalix.Codec.Tests.Serialization;

public sealed class LiteSerializerPrimitiveTests
{
    [Theory]
    [InlineData(123)]
    [InlineData(0)]
    [InlineData(-999)]
    public void SerializeDeserialize_Int32_RoundTripsValue(int input)
    {
        int output = LiteSerializerRoundTrip.RoundTrip(input);
        Assert.Equal(input, output);
    }

    [Theory]
    [InlineData(3.14)]
    [InlineData(0.0)]
    [InlineData(-1.23)]
    public void SerializeDeserialize_Double_RoundTripsValue(double input)
    {
        double output = LiteSerializerRoundTrip.RoundTrip(input);
        Assert.Equal(input, output, precision: 5);
    }

    [Fact]
    public void SerializeDeserialize_Boolean_RoundTripsValue()
    {
        bool result = LiteSerializerRoundTrip.RoundTrip(true);
        Assert.True(result);
    }
}

















