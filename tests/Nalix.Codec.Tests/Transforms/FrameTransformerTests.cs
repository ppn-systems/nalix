// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.LZ4;
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

public sealed class FrameTransformerTests
{
    [Theory]
    [InlineData(CipherSuiteType.Chacha20, 10)]
    [InlineData(CipherSuiteType.Chacha20Poly1305, 10)]
    [InlineData(CipherSuiteType.Salsa20, 24)]
    [InlineData(CipherSuiteType.Salsa20Poly1305, 24)]
    public void GetMaxCiphertextSize_WhenSuiteIsValid_ReturnsExpectedEnvelopeCapacity(CipherSuiteType suite, int plaintextSize)
    {
        int expected = EnvelopeCipher.HeaderSize
            + EnvelopeCipher.GetNonceLength(suite)
            + plaintextSize
            + EnvelopeCipher.GetTagLength(suite);

        int actual = FrameTransformer.GetMaxCiphertextSize(suite, plaintextSize);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("payload payload payload payload payload")]
    public void Compress_ThenDecompress_RoundTripsPayloadAndHeader(string payloadText)
    {
        byte[] packetBytes = FrameTestData.CreatePacketBytes(payloadText);
        using BufferLease source = BufferLease.CopyFrom(packetBytes);
        using BufferLease compressed = BufferLease.Rent(FrameTransformer.Offset + FrameTransformer.GetMaxCompressedSize(source.Length - FrameTransformer.Offset));
        using BufferLease decompressed = BufferLease.Rent(packetBytes.Length);

        FrameTransformer.Compress(source, compressed);
        int decompressedLength = FrameTransformer.GetDecompressedLength(compressed.Span[FrameTransformer.Offset..]);
        FrameTransformer.Decompress(compressed, decompressed);

        Assert.Equal(source.Length - FrameTransformer.Offset, decompressedLength);
        Assert.Equal(packetBytes, decompressed.Memory.ToArray());
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20)]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    public void Encrypt_ThenDecrypt_RoundTripsPayloadAndHeader(CipherSuiteType suite)
    {
        byte[] packetBytes = FrameTestData.CreatePacketBytes("encrypted payload");
        byte[] key = [.. Enumerable.Range(1, 32).Select(static x => (byte)x)];
        using BufferLease source = BufferLease.CopyFrom(packetBytes);
        using BufferLease encrypted = BufferLease.Rent(FrameTransformer.Offset + FrameTransformer.GetMaxCiphertextSize(suite, source.Length - FrameTransformer.Offset));
        using BufferLease decrypted = BufferLease.Rent(packetBytes.Length);

        FrameTransformer.Encrypt(source, encrypted, key, null, suite);
        int plaintextLength = FrameTransformer.GetPlaintextLength(encrypted.Span);
        FrameTransformer.Decrypt(encrypted, decrypted, key, suite, out _);

        Assert.Equal(source.Length - FrameTransformer.Offset, plaintextLength);
        Assert.Equal(packetBytes, decrypted.Memory.ToArray());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Encrypt_WhenKeyOrSourceIsInvalid_ThrowsCipherOrInternalError(bool useEmptyKey, bool useShortSource)
    {
        byte[] packetBytes = FrameTestData.CreatePacketBytes("abc");
        byte[] key = useEmptyKey ? [] : [.. Enumerable.Repeat((byte)7, 32)];
        using BufferLease source = useShortSource
            ? BufferLease.Rent(FrameTransformer.Offset - 1)
            : BufferLease.CopyFrom(packetBytes);
        using BufferLease destination = BufferLease.Rent(
            FrameTransformer.Offset + FrameTransformer.GetMaxCiphertextSize(CipherSuiteType.Chacha20, Math.Max(1, packetBytes.Length - FrameTransformer.Offset)));

        if (useShortSource)
        {
            source.CommitLength(FrameTransformer.Offset - 1);
        }

        if (useEmptyKey)
        {
            _ = Assert.ThrowsAny<CipherException>(() => FrameTransformer.Encrypt(source, destination, key, null, CipherSuiteType.Chacha20));
        }
        else
        {
            _ = Assert.ThrowsAny<InternalErrorException>(() => FrameTransformer.Encrypt(source, destination, key, null, CipherSuiteType.Chacha20));
        }
    }

    [Theory]
    [InlineData("tiny")]
    [InlineData("payload near edge")]
    public void Compress_WhenDestinationHasMinimumReportedCapacity_Succeeds(string payloadText)
    {
        byte[] packetBytes = FrameTestData.CreatePacketBytes(payloadText);
        using BufferLease source = BufferLease.CopyFrom(packetBytes);
        using BufferLease compressed = BufferLease.Rent(FrameTransformer.Offset + FrameTransformer.GetMaxCompressedSize(source.Length - FrameTransformer.Offset));
        using BufferLease decompressed = BufferLease.Rent(packetBytes.Length);

        FrameTransformer.Compress(source, compressed);
        FrameTransformer.Decompress(compressed, decompressed);

        Assert.Equal(packetBytes, decompressed.Memory.ToArray());
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20)]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    public void Encrypt_WhenDestinationHasMinimumReportedCapacity_Succeeds(CipherSuiteType suite)
    {
        byte[] packetBytes = FrameTestData.CreatePacketBytes("edge");
        byte[] key = [.. Enumerable.Range(1, 32).Select(static x => (byte)x)];
        using BufferLease source = BufferLease.CopyFrom(packetBytes);
        using BufferLease encrypted = BufferLease.Rent(FrameTransformer.Offset + FrameTransformer.GetMaxCiphertextSize(suite, source.Length - FrameTransformer.Offset));
        using BufferLease decrypted = BufferLease.Rent(packetBytes.Length);

        FrameTransformer.Encrypt(source, encrypted, key, null, suite);
        FrameTransformer.Decrypt(encrypted, decrypted, key, suite, out _);

        Assert.True(decrypted.Length >= packetBytes.Length);
        Assert.Equal(packetBytes, decrypted.Memory.Span[..packetBytes.Length].ToArray());
    }

    [Theory]
    [InlineData(LZ4CompressionConstants.MaxBlockSize + 1)]
    [InlineData(32 * 1024 * 1024)]
    public void GetDecompressedLength_WhenOriginalLengthExceedsMaxBlockSize_RejectsBeforeAllocation(int fakeOriginalLength)
    {
        byte[] block = new byte[LZ4BlockHeader.Size + 16];
        System.BitConverter.GetBytes(fakeOriginalLength).CopyTo(block, 0);
        System.BitConverter.GetBytes(block.Length).CopyTo(block, 4);

        Assert.False(FrameTransformer.TryGetDecompressedLength(block, out int length));
        Assert.Equal(0, length);
        _ = Assert.ThrowsAny<LZ4Exception>(() => FrameTransformer.GetDecompressedLength(block));
    }

    [Fact]
    public void GetDecompressedLength_WhenCompressedLengthIsMismatched_RejectsBeforeAllocation()
    {
        byte[] block = new byte[LZ4BlockHeader.Size + 8];
        System.BitConverter.GetBytes(16).CopyTo(block, 0);
        System.BitConverter.GetBytes(block.Length + 4).CopyTo(block, 4); // Mismatched CompressedLength

        Assert.False(FrameTransformer.TryGetDecompressedLength(block, out int length));
        Assert.Equal(0, length);
        _ = Assert.ThrowsAny<LZ4Exception>(() => FrameTransformer.GetDecompressedLength(block));
    }

    [Fact]
    public void GetDecompressedLength_WhenExpansionRatioIsImpossible_RejectsBeforeAllocation()
    {
        // 4 bytes of LZ4 token stream can mathematically expand to at most 19 bytes.
        byte[] block = new byte[LZ4BlockHeader.Size + 4];
        System.BitConverter.GetBytes(20).CopyTo(block, 0);
        System.BitConverter.GetBytes(block.Length).CopyTo(block, 4);

        Assert.False(FrameTransformer.TryGetDecompressedLength(block, out int length));
        Assert.Equal(0, length);
        _ = Assert.ThrowsAny<LZ4Exception>(() => FrameTransformer.GetDecompressedLength(block));

        // Exact boundary (19 bytes for 4-byte stream) passes header validation:
        System.BitConverter.GetBytes(19).CopyTo(block, 0);
        Assert.True(FrameTransformer.TryGetDecompressedLength(block, out int validLength));
        Assert.Equal(19, validLength);
    }
}
