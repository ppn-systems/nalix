// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.Serialization;

namespace Nalix.Codec.Tests.TestSupport;

public static class LiteSerializerRoundTrip
{
    public static T RoundTrip<T>(T value)
    {
        byte[] buffer = LiteSerializer.Serialize(value);
        T output = default!;
        _ = LiteSerializer.Deserialize(buffer, ref output);
        return output;
    }

    public static int RoundTrip<T>(T value, ref T destination)
    {
        byte[] buffer = LiteSerializer.Serialize(value);
        return LiteSerializer.Deserialize(buffer, ref destination);
    }
}

















