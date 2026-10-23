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
using Nalix.Environment.Options;
using Nalix.Environment.Random;
using System.Text;

namespace Nalix.Codec.Tests.DataFrames;

public sealed class FragmentOptionsTests
{
    [Theory]
    [InlineData(8192, 8192, true)]
    [InlineData(0, 8192, false)]
    [InlineData(4096, 8192, false)]
    public void Validate_WhenPayloadAndChunkSizesVary_MatchesExpectedValidity(
        int maxPayloadSize,
        int maxChunkSize,
        bool shouldSucceed)
    {
        FragmentOptions options = new()
        {
            MaxPayloadSize = maxPayloadSize,
            MaxChunkSize = maxChunkSize
        };

        Exception? exception = Record.Exception(options.Validate);

        if (shouldSucceed)
        {
            Assert.Null(exception);
        }
        else
        {
            _ = Assert.IsType<Nalix.Abstractions.Exceptions.ValidationException>(exception);
        }
    }
}
