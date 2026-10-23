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

public sealed class FragmentHeaderTests
{
    [Theory]
    [InlineData((ushort)1, (ushort)0, (ushort)1, true)]
    [InlineData((ushort)2, (ushort)1, (ushort)3, false)]
    public void WriteTo_ThenReadFrom_PreservesPublicState(ushort streamId, ushort chunkIndex, ushort totalChunks, bool isLast)
    {
        FragmentHeader header = new(streamId, chunkIndex, totalChunks, isLast);
        byte[] buffer = new byte[FragmentHeader.WireSize];

        header.WriteTo(buffer);
        FragmentHeader roundTripped = FragmentHeader.ReadFrom(buffer);

        Assert.Equal(header, roundTripped);
        Assert.Equal(streamId, roundTripped.StreamId);
        Assert.Equal(chunkIndex, roundTripped.ChunkIndex);
        Assert.Equal(totalChunks, roundTripped.TotalChunks);
        Assert.Equal(isLast, roundTripped.IsLast);
    }

    [Fact]
    public void ReadFrom_WhenMagicIsInvalid_ThrowsInvalidDataException()
    {
        byte[] buffer = new byte[FragmentHeader.WireSize];

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => FragmentHeader.ReadFrom(buffer));

        Assert.Equal("Invalid fragment magic", exception.Message);
    }
}
