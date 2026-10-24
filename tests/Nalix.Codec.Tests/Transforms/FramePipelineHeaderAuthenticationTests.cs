// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Transforms;

/// <summary>
/// Verifies that the clear-text packet header is bound to the AEAD tag, so an on-path
/// attacker cannot rewrite OpCode, Flags, Priority or SequenceId of an encrypted frame.
/// </summary>
public sealed class FramePipelineHeaderAuthenticationTests
{
    private const CipherSuiteType Suite = CipherSuiteType.Chacha20Poly1305;

    private static readonly byte[] Key = CreateKey();

    public static TheoryData<int> HeaderByteOffsets => new()
    {
        (int)PacketHeaderOffset.OpCode,
        (int)PacketHeaderOffset.OpCode + 1,
        (int)PacketHeaderOffset.Flags,
        (int)PacketHeaderOffset.Priority,
        (int)PacketHeaderOffset.SequenceId,
        (int)PacketHeaderOffset.SequenceId + 1,
    };

    [Theory]
    [MemberData(nameof(HeaderByteOffsets))]
    public void TryProcessInbound_WhenEncryptedHeaderByteTampered_ReturnsFalse(int offset)
    {
        byte[] wire = Seal(CreateFrame(64, compressible: false), compress: false);
        Assert.Equal(0, wire[(int)PacketHeaderOffset.Flags] & (int)PacketFlags.COMPRESSED);

        Assert.False(Open(Tamper(wire, offset)));
    }

    [Theory]
    [MemberData(nameof(HeaderByteOffsets))]
    public void TryProcessInbound_WhenFusedCompressedEncryptedHeaderByteTampered_ReturnsFalse(int offset)
    {
        byte[] wire = Seal(CreateFrame(512, compressible: true), compress: true);
        Assert.NotEqual(0, wire[(int)PacketHeaderOffset.Flags] & (int)PacketFlags.COMPRESSED);

        Assert.False(Open(Tamper(wire, offset)));
    }

    [Theory]
    [MemberData(nameof(HeaderByteOffsets))]
    public void TryDecryptFrame_WhenHeaderByteTampered_ReturnsFalse(int offset)
    {
        using BufferLease src = ToLease(CreateFrame(64, compressible: false));
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src, Key, 7u, Suite);
        using BufferLease tampered = ToLease(Tamper(encrypted.Span.ToArray(), offset));

        Assert.False(FrameCipher.TryDecryptFrame(tampered, Key, Suite, out _, out IBufferLease? dest));
        Assert.Null(dest);
    }

    [Theory]
    [MemberData(nameof(HeaderByteOffsets))]
    public void DecryptFrame_WhenHeaderByteTampered_ThrowsException(int offset)
    {
        using BufferLease src = ToLease(CreateFrame(64, compressible: false));
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src, Key, 7u, Suite);
        using BufferLease tampered = ToLease(Tamper(encrypted.Span.ToArray(), offset));

        _ = Assert.ThrowsAny<Exception>(() => FrameCipher.DecryptFrame(tampered, Key, Suite, out _));
    }

    [Theory]
    [InlineData(64, false, false)]
    [InlineData(512, true, true)]
    public void TryProcessInbound_WhenHeaderUntouched_RoundTripsFrame(int payloadSize, bool compressible, bool compress)
    {
        byte[] frame = CreateFrame(payloadSize, compressible);
        byte[] wire = Seal(frame, compress);

        IBufferLease current = ToLease(wire);
        IBufferLease original = current;
        try
        {
            Assert.True(FramePipeline.TryProcessInbound(ref current, Key, Suite, out uint? seq));
            Assert.Equal(7u, seq);
            Assert.Equal(frame, current.Span.ToArray());
        }
        finally
        {
            if (!ReferenceEquals(current, original))
            {
                current.Dispose();
            }
            original.Dispose();
        }
    }

    private static byte[] CreateKey()
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)((i * 7) + 1);
        }
        return key;
    }

    private static byte[] CreateFrame(int payloadSize, bool compressible)
    {
        byte[] frame = new byte[FrameTransformer.Offset + payloadSize];
        frame[(int)PacketHeaderOffset.OpCode] = 0x34;
        frame[(int)PacketHeaderOffset.OpCode + 1] = 0x12;
        frame[(int)PacketHeaderOffset.Flags] = (byte)PacketFlags.RELIABLE;
        frame[(int)PacketHeaderOffset.Priority] = 1;
        frame[(int)PacketHeaderOffset.SequenceId] = 0x78;
        frame[(int)PacketHeaderOffset.SequenceId + 1] = 0x56;
        for (int i = FrameTransformer.Offset; i < frame.Length; i++)
        {
            frame[i] = compressible ? (byte)'A' : (byte)((i * 31) + 17);
        }
        return frame;
    }

    private static BufferLease ToLease(byte[] bytes)
    {
        BufferLease lease = BufferLease.Rent(bytes.Length);
        bytes.CopyTo(lease.SpanFull);
        lease.CommitLength(bytes.Length);
        return lease;
    }

    private static byte[] Seal(byte[] frame, bool compress)
    {
        IBufferLease current = ToLease(frame);
        IBufferLease original = current;
        try
        {
            FramePipeline.ProcessOutbound(ref current, compress, 1, true, Key, 7u, Suite);
            return current.Span.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(current, original))
            {
                current.Dispose();
            }
            original.Dispose();
        }
    }

    private static byte[] Tamper(byte[] wire, int offset)
    {
        byte[] copy = (byte[])wire.Clone();

        // Flip a bit that keeps ENCRYPTED/COMPRESSED intact so the frame still takes the decrypt path.
        copy[offset] ^= offset == (int)PacketHeaderOffset.Flags ? (byte)PacketFlags.RELIABLE : (byte)0x01;
        return copy;
    }

    private static bool Open(byte[] wire)
    {
        IBufferLease current = ToLease(wire);
        IBufferLease original = current;
        try
        {
            return FramePipeline.TryProcessInbound(ref current, Key, Suite, out _);
        }
        finally
        {
            if (!ReferenceEquals(current, original))
            {
                current.Dispose();
            }
            original.Dispose();
        }
    }
}
