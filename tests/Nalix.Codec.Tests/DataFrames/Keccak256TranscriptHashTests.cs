// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Codec.Tests.TestSupport;
using Nalix.Codec.Transforms;
using Nalix.Environment.Extensions;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Random;
using System.Text;

namespace Nalix.Codec.Tests.DataFrames;

public sealed class Keccak256TranscriptHashTests
{
    [Fact]
    public void HashDataToFixed_WhenGivenSameInput_ReturnsSameBytes32()
    {
        byte[] transcript = [1, 2, 3, 4, 5, 6, 7, 8, 9];

        Bytes32 first = Nalix.Codec.Security.Hashing.Keccak256.HashDataToFixed(transcript);
        Bytes32 second = Nalix.Codec.Security.Hashing.Keccak256.HashDataToFixed(transcript);

        Assert.Equal(first, second);
    }
}
