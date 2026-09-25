// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Extensions;
using Nalix.Environment.Memory;
using Nalix.Environment.Random;

namespace Nalix.Codec.Tests.DataFrames;

public sealed class PacketTransformTests
{
    private static readonly byte[] s_testKey = new byte[32];

    static PacketTransformTests() => Csprng.NextBytes(s_testKey);

    [Fact]
    public void FrameCipher_Roundtrip_ShouldSucceed()
    {
        // 1. Arrange
        byte[] originalPayload = new byte[100];
        Csprng.NextBytes(originalPayload);

        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset + originalPayload.Length);
        src.CommitLength(FrameTransformer.Offset + originalPayload.Length);

        // Zero the header area and set flags
        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.NONE };

        // Copy payload
        originalPayload.CopyTo(src.Span[FrameTransformer.Offset..]);

        // 2. Encrypt
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src, s_testKey, null, CipherSuiteType.Chacha20Poly1305);

        Assert.True(encrypted.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.ENCRYPTED));
        Assert.NotEqual(0, encrypted.Length);
        Assert.True(encrypted.Length >= FrameTransformer.Offset + EnvelopeCipher.HeaderSize);

        // 3. Decrypt
        using IBufferLease decrypted = FrameCipher.DecryptFrame(encrypted, s_testKey, CipherSuiteType.Chacha20Poly1305, out _);

        // ENCRYPTED is transform-internal bookkeeping and is cleared post-decrypt;
        // the dedicated EncryptedOnWire signal is the on-wire audit trail instead.
        Assert.False(decrypted.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.ENCRYPTED));
        Assert.True(decrypted.EncryptedOnWire);
        Assert.Equal(src.Length, decrypted.Length);

        byte[] resultPayload = decrypted.Span[FrameTransformer.Offset..].ToArray();
        Assert.Equal(originalPayload, resultPayload);
    }

    [Fact]
    public void FrameCipher_ZeroPayload_RoundtripShouldSucceed()
    {
        // Regression for #300: a header-only frame (Length == Offset, no payload)
        // must encrypt/decrypt symmetrically instead of tripping SourceTooSmall.
        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset);
        src.CommitLength(FrameTransformer.Offset);

        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.NONE };

        using IBufferLease encrypted = FrameCipher.EncryptFrame(src, s_testKey, null, CipherSuiteType.Chacha20Poly1305);

        Assert.True(encrypted.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.ENCRYPTED));

        using IBufferLease decrypted = FrameCipher.DecryptFrame(encrypted, s_testKey, CipherSuiteType.Chacha20Poly1305, out _);

        Assert.False(decrypted.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.ENCRYPTED));
        Assert.True(decrypted.EncryptedOnWire);
        Assert.Equal(FrameTransformer.Offset, decrypted.Length);
    }

    [Fact]
    public void FrameCompression_Roundtrip_ShouldSucceed()
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
    public void PacketTransform_Combined_ShouldSucceed()
    {
        // 1. Arrange
        byte[] originalPayload = new byte[500];
        Csprng.NextBytes(originalPayload);

        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset + originalPayload.Length);
        src.CommitLength(FrameTransformer.Offset + originalPayload.Length);

        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.NONE };
        originalPayload.CopyTo(src.Span[FrameTransformer.Offset..]);

        // 2. Encrypt then Compress
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src, s_testKey, null, CipherSuiteType.Chacha20Poly1305);
        using IBufferLease compressed = FrameCompression.CompressFrame(encrypted);

        Assert.True(compressed.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.ENCRYPTED));
        Assert.True(compressed.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.COMPRESSED));

        // 3. Decompress then Decrypt
        using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed);
        using IBufferLease decrypted = FrameCipher.DecryptFrame(decompressed, s_testKey, CipherSuiteType.Chacha20Poly1305, out _);

        Assert.False(decrypted.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.ENCRYPTED));
        Assert.False(decrypted.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.COMPRESSED));
        Assert.True(decrypted.EncryptedOnWire);
        Assert.Equal(src.Length, decrypted.Length);

        byte[] resultPayload = decrypted.Span[FrameTransformer.Offset..].ToArray();
        Assert.Equal(originalPayload, resultPayload);
    }

    [Fact]
    public void FramePipeline_CompressAndEncryptFused_RoundtripShouldSucceed()
    {
        // Compressible payload: incompressible data is now sent without the COMPRESSED flag
        // (covered by the *_IncompressiblePayload_* tests below).
        byte[] originalPayload = new byte[1024];
        for (int i = 0; i < originalPayload.Length; i++)
        {
            originalPayload[i] = (byte)(i % 13);
        }

        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset + originalPayload.Length);
        src.CommitLength(FrameTransformer.Offset + originalPayload.Length);

        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.NONE };
        originalPayload.CopyTo(src.Span[FrameTransformer.Offset..]);

        IBufferLease outbound = src;
        FramePipeline.ProcessOutbound(
            ref outbound,
            enableCompress: true,
            minSizeToCompress: 1,
            enableEncrypt: true,
            secret: s_testKey,
            seq: 1,
            algorithm: CipherSuiteType.Chacha20Poly1305);

        using IBufferLease transformed = outbound;

        PacketFlags transformedFlags = transformed.Span.AsHeaderRef().Flags;
        Assert.True(transformedFlags.HasFlag(PacketFlags.NONE));
        Assert.True(transformedFlags.HasFlag(PacketFlags.COMPRESSED));
        Assert.True(transformedFlags.HasFlag(PacketFlags.ENCRYPTED));

        IBufferLease inbound = transformed;
        FramePipeline.ProcessInbound(ref inbound, s_testKey, CipherSuiteType.Chacha20Poly1305, out _);

        using IBufferLease restored = inbound;

        PacketFlags restoredFlags = restored.Span.AsHeaderRef().Flags;
        Assert.True(restoredFlags.HasFlag(PacketFlags.NONE));
        Assert.False(restoredFlags.HasFlag(PacketFlags.COMPRESSED));
        Assert.False(restoredFlags.HasFlag(PacketFlags.ENCRYPTED));
        Assert.True(restored.EncryptedOnWire);
        Assert.Equal(src.Length, restored.Length);
        Assert.Equal(src.Span.ToArray(), restored.Span.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FramePipeline_IncompressiblePayload_IsSentWithoutCompressedFlag(bool encrypt)
    {
        byte[] originalPayload = new byte[1024];
        Csprng.NextBytes(originalPayload);

        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset + originalPayload.Length);
        src.CommitLength(FrameTransformer.Offset + originalPayload.Length);
        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.NONE };
        originalPayload.CopyTo(src.Span[FrameTransformer.Offset..]);

        IBufferLease outbound = src;
        FramePipeline.ProcessOutbound(
            ref outbound,
            enableCompress: true,
            minSizeToCompress: 1,
            enableEncrypt: encrypt,
            secret: s_testKey,
            seq: encrypt ? 7u : null,
            algorithm: encrypt ? CipherSuiteType.Chacha20Poly1305 : CipherSuiteType.None);

        PacketFlags flags = outbound.Span.AsHeaderRef().Flags;
        Assert.False(flags.HasFlag(PacketFlags.COMPRESSED));
        Assert.Equal(encrypt, flags.HasFlag(PacketFlags.ENCRYPTED));

        if (!encrypt)
        {
            // Compression was attempted and discarded: the original lease is sent as-is.
            Assert.Same(src, outbound);
            Assert.Equal(originalPayload, outbound.Span[FrameTransformer.Offset..].ToArray());
            return;
        }

        IBufferLease inbound = outbound;
        FramePipeline.ProcessInbound(ref inbound, s_testKey, CipherSuiteType.Chacha20Poly1305, out uint? seq);

        using IBufferLease restored = inbound;
        Assert.Equal(7u, seq);
        Assert.True(restored.EncryptedOnWire);
        Assert.Equal(src.Span.ToArray(), restored.Span.ToArray());
        outbound.Dispose();
    }

    [Fact]
    public void FramePipeline_CompressiblePayload_CompressOnly_SetsCompressedFlagAndRoundTrips()
    {
        byte[] originalPayload = new byte[2048];
        for (int i = 0; i < originalPayload.Length; i++)
        {
            originalPayload[i] = (byte)(i / 32);
        }

        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset + originalPayload.Length);
        src.CommitLength(FrameTransformer.Offset + originalPayload.Length);
        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.NONE };
        originalPayload.CopyTo(src.Span[FrameTransformer.Offset..]);

        IBufferLease outbound = src;
        FramePipeline.ProcessOutbound(ref outbound, true, 1, false, default, null, CipherSuiteType.None);

        using IBufferLease compressed = outbound;
        Assert.NotSame(src, compressed);
        Assert.True(compressed.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.COMPRESSED));
        Assert.True(compressed.Length < src.Length / 2);

        IBufferLease inbound = compressed;
        FramePipeline.ProcessInbound(ref inbound, default, CipherSuiteType.None, out _);
        using IBufferLease restored = inbound;
        Assert.Equal(originalPayload, restored.Span[FrameTransformer.Offset..].ToArray());
    }
}



















