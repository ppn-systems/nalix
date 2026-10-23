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

public sealed class FrameCipherTests
{
    private static readonly byte[] s_testKey = new byte[32];

    static FrameCipherTests() => Csprng.NextBytes(s_testKey);

    [Fact]
    public void EncryptFrame_ThenDecryptFrame_RestoresPayload()
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
    public void EncryptFrame_WhenFrameHasNoPayload_RoundTripsHeaderOnlyFrame()
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
    public void EncryptOrDecryptFrame_WhenSourceIsNull_ThrowsArgumentNullException()
    {
        byte[] key = new byte[32];
        _ = Assert.Throws<ArgumentNullException>(() => FrameCipher.EncryptFrame(null!, key, null, CipherSuiteType.Chacha20Poly1305));
        _ = Assert.Throws<ArgumentNullException>(() => FrameCipher.DecryptFrame(null!, key, CipherSuiteType.Chacha20Poly1305, out _));
    }

    [Fact]
    public void DecryptFrame_WhenFrameCarriesOtherFlags_RemovesOnlyEncryptedFlag()
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i + 1);
        }

        byte[] payload = [1, 2, 3, 4, 5, 6];
        using BufferLease src = BufferLease.Rent(FrameTransformer.Offset + payload.Length);
        src.CommitLength(FrameTransformer.Offset + payload.Length);
        src.Span[..FrameTransformer.Offset].Clear();
        src.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.COMPRESSED };
        payload.CopyTo(src.Span[FrameTransformer.Offset..]);

        using IBufferLease encrypted = FrameCipher.EncryptFrame(src, key, null, CipherSuiteType.Chacha20Poly1305);
        using IBufferLease decrypted = FrameCipher.DecryptFrame(encrypted, key, CipherSuiteType.Chacha20Poly1305, out _);

        PacketFlags flags = decrypted.Span.AsHeaderRef().Flags;
        Assert.True(flags.HasFlag(PacketFlags.COMPRESSED));
        Assert.False(flags.HasFlag(PacketFlags.ENCRYPTED));
        Assert.True(decrypted.EncryptedOnWire);
    }

    [Fact]
    public void DecryptFrame_WhenFrameHoldsOnlyHeader_ThrowsCipherException()
    {
        byte[] key = new byte[32];
        using BufferLease shortFrame = BufferLease.Rent(FrameTransformer.Offset);
        shortFrame.CommitLength(FrameTransformer.Offset);

        _ = Assert.ThrowsAny<CipherException>(() => FrameCipher.DecryptFrame(shortFrame, key, CipherSuiteType.Chacha20, out _));
    }

    [Fact]
    public void DecryptFrame_WhenCiphertextIsShorterThanEnvelope_ThrowsCipherException()
    {
        byte[] key = [.. Enumerable.Range(1, 32).Select(static x => (byte)x)];
        using BufferLease source = BufferLease.CopyFrom(new byte[FrameTransformer.Offset + 1]);

        _ = Assert.ThrowsAny<CipherException>(() => FrameCipher.DecryptFrame(source, key, CipherSuiteType.Chacha20, out _));
    }
}
