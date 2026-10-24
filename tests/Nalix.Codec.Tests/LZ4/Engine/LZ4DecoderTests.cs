// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.LZ4;

namespace Nalix.Codec.Tests.LZ4.Engine;

public sealed class LZ4DecoderTests
{
    [Fact]
    public void Decode_WhenMatchOverlapsItsOwnOutput_RestoresOriginal()
    {
        // Arrange: Create a payload with long repeating pattern (RLE-like)
        // This will force the encoder to use a match with offset = 1 and large length.
        byte[] original = [.. Enumerable.Repeat((byte)'A', 1000)];

        int maxCompressedLength = LZ4BlockEncoder.GetMaxLength(original.Length);
        byte[] compressed = new byte[maxCompressedLength];
        byte[] decompressed = new byte[original.Length];

        // Act
        int writtenCompressed = LZ4Codec.Encode(original, compressed);
        int writtenDecompressed = LZ4Codec.Decode(compressed.AsSpan(0, writtenCompressed), decompressed);

        // Assert
        Assert.Equal(original.Length, writtenDecompressed);
        Assert.Equal(original, decompressed);
    }
}


