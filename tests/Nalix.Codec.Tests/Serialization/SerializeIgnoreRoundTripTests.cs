// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Nalix.Codec.Serialization;
using Nalix.Codec.Tests.TestSupport;

namespace Nalix.Codec.Tests.Serialization;

public sealed class SerializeIgnoreRoundTripTests
{
    private const string Secret = "SECRET-PASSWORD-HASH";

    [Fact]
    public void Serialize_IgnoredPropertySharingTypeWithOrderedField_OmitsSecretFromBytes()
    {
        SerializeIgnoreLogin value = new() { Username = "alice", PasswordHash = Secret };

        byte[] buffer = LiteSerializer.Serialize(value);

        Assert.DoesNotContain(Encoding.UTF8.GetBytes(Secret), buffer);
        Assert.Contains(Encoding.UTF8.GetBytes("alice"), buffer);
    }

    [Fact]
    public void RoundTrip_IgnoredPropertySharingTypeWithOrderedField_RestoresOnlyOrderedMembers()
    {
        SerializeIgnoreLogin input = new() { Username = "alice", PasswordHash = Secret };

        SerializeIgnoreLogin output = LiteSerializerRoundTrip.RoundTrip(input);

        Assert.Equal("alice", output.Username);
        Assert.Equal("", output.PasswordHash);
    }
}
