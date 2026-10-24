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

namespace Nalix.Codec.Tests.Transforms;

public sealed class FrameCompressionTests
{
    [Fact]
    public void CompressFrame_ThenDecompressFrame_RestoresPayload()
    {
        // 1. Arrange
        byte[] originalPayload = new byte[1000]; // Larger payload for compression
        Csprng.NextBytes(originalPayload);

        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset + originalPayload.Length);
        src.CommitLength(FrameTransformer.Offset + originalPayload.Length);

        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.NONE };
        originalPayload.CopyTo(src.Span[FrameTransformer.Offset..]);

        // 2. Compress
        using IBufferLease compressed = FrameCompression.CompressFrame(src);

        Assert.True(compressed.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.COMPRESSED));

        // 3. Decompress
        using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed);

        Assert.False(decompressed.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.COMPRESSED));
        Assert.Equal(src.Length, decompressed.Length);

        byte[] resultPayload = decompressed.Span[FrameTransformer.Offset..].ToArray();
        Assert.Equal(originalPayload, resultPayload);
    }

    [Fact]
    public void CompressOrDecompressFrame_WhenSourceIsNull_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => FrameCompression.CompressFrame(null!));
        _ = Assert.Throws<ArgumentNullException>(() => FrameCompression.DecompressFrame(null!));
    }

    [Fact]
    public void DecompressFrame_WhenFrameCarriesOtherFlags_RemovesOnlyCompressedFlag()
    {
        byte[] payload = new byte[128];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 17);
        }

        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset + payload.Length);
        src.CommitLength(FrameTransformer.Offset + payload.Length);
        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.ENCRYPTED };
        payload.CopyTo(src.Span[FrameTransformer.Offset..]);

        using IBufferLease compressed = FrameCompression.CompressFrame(src);
        using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed);

        PacketFlags flags = decompressed.Span.AsHeaderRef().Flags;
        Assert.True(flags.HasFlag(PacketFlags.ENCRYPTED));
        Assert.False(flags.HasFlag(PacketFlags.COMPRESSED));
    }
}
