// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Serialization;
using Nalix.Codec.Tests.TestSupport;

namespace Nalix.Codec.Tests.Serialization;

public sealed class LiteSerializerReferenceTypeTests
{
    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("xin chào tiếng Việt")]
    [InlineData("😀 unicode")]
    public void SerializeDeserialize_StringReference_RoundTripsValue(string input)
    {
        byte[] data = LiteSerializer.Serialize(input);
        string output = LiteSerializerRoundTrip.RoundTrip(input);

        Assert.Equal(data.Length, data.Length);
        Assert.Equal(input, output);
    }

}

















