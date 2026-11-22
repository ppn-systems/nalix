// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Extensions;
using Nalix.Environment.Memory;
using Nalix.Environment.Random;
using Xunit;

namespace Nalix.Codec.Tests.Transforms;

/// <summary>
/// The outbound path backs off from compressing an opcode whose payloads keep failing to compress, and probes it
/// again now and then. The learned state is process-wide, so these tests run alone and use their own opcodes.
/// </summary>
[Collection(TestCollections.CompressionStats)]
public sealed class AdaptiveCompressionTests : IDisposable
{
    private const ushort Op = 0x6A5E;
    private const ushort OtherOp = 0x6A5F;

    private static readonly byte[] s_key = CreateKey();

    public AdaptiveCompressionTests() => AdaptiveCompression.Reset();

    public void Dispose() => AdaptiveCompression.Reset();

    [Fact]
    public void ProcessOutbound_AfterRepeatedIncompressiblePayloads_BacksOffToOneAttemptPerInterval()
    {
        for (int i = 0; i < AdaptiveCompression.MissesBeforeBackoff - 1; i++)
        {
            Assert.False(SendIsCompressed(Op, compressible: false, encrypt: false));
            Assert.Equal(1, AdaptiveCompression.Interval(Op));
        }

        Assert.False(SendIsCompressed(Op, compressible: false, encrypt: false));
        Assert.Equal(AdaptiveCompression.MinSkipFrames, AdaptiveCompression.Interval(Op));

        // Any run of Interval consecutive frames holds exactly one attempt.
        Assert.Equal(1, CountAttempts(Op, AdaptiveCompression.MinSkipFrames));
        Assert.Equal(1, CountAttempts(Op, AdaptiveCompression.MinSkipFrames));
    }

    [Fact]
    public void ProcessOutbound_WhileSkipping_SendsCompressiblePayloadUncompressedAndIntact()
    {
        BackOff(Op);
        AlignToNextProbe(Op);

        byte[] payload = CompressiblePayload();
        IBufferLease outbound = NewFrame(Op, payload);
        IBufferLease original = outbound;
        FramePipeline.ProcessOutbound(ref outbound, true, 1, false, default, null, CipherSuiteType.None);

        Assert.Same(original, outbound);
        Assert.False(outbound.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.COMPRESSED));
        Assert.Equal(payload, outbound.Span[FrameTransformer.Offset..].ToArray());
        original.Dispose();
    }

    [Fact]
    public void Record_WhenProbePaysOff_CompressesTheOpcodeAgain()
    {
        BackOff(Op);

        AdaptiveCompression.Record(Op, worthwhile: true);

        Assert.Equal(1, AdaptiveCompression.Interval(Op));
        Assert.Equal(AdaptiveCompression.MinSkipFrames * 2, CountAttempts(Op, AdaptiveCompression.MinSkipFrames * 2));
        Assert.True(SendIsCompressed(Op, compressible: true, encrypt: false));
    }

    [Fact]
    public void Record_WhenProbeFailsAgain_DoublesTheIntervalUpToTheMaximum()
    {
        BackOff(Op);

        int expected = AdaptiveCompression.MinSkipFrames;
        for (int round = 0; round < 8; round++)
        {
            Assert.Equal(expected, AdaptiveCompression.Interval(Op));

            // One failed probe is enough to double the interval.
            AdaptiveCompression.Record(Op, worthwhile: false);
            expected = Math.Min(expected * 2, AdaptiveCompression.MaxSkipFrames);
        }

        Assert.Equal(AdaptiveCompression.MaxSkipFrames, AdaptiveCompression.Interval(Op));
        Assert.Equal(1, CountAttempts(Op, AdaptiveCompression.MaxSkipFrames));
    }

    [Fact]
    public void Record_IncompressibleOpcode_DoesNotAffectAnyOtherOpcode()
    {
        BackOff(Op);

        // Every other opcode keeps its own entry, including the ones that differ only in the high bits.
        Assert.Equal(1, AdaptiveCompression.Interval(OtherOp));
        Assert.Equal(1, AdaptiveCompression.Interval((ushort)(Op ^ 0x1000)));
        Assert.Equal(1, AdaptiveCompression.Interval((ushort)(Op & 0x0FFF)));
        Assert.True(SendIsCompressed(OtherOp, compressible: true, encrypt: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessOutbound_FusedAndPlainPaths_BothLearnAndStillRoundTrip(bool encrypt)
    {
        for (int i = 0; i < AdaptiveCompression.MissesBeforeBackoff; i++)
        {
            Assert.False(SendIsCompressed(Op, compressible: false, encrypt));
        }

        Assert.Equal(AdaptiveCompression.MinSkipFrames, AdaptiveCompression.Interval(Op));
        AlignToNextProbe(Op);

        // A skipped frame takes the uncompressed path and still decodes on the receiving side.
        byte[] payload = CompressiblePayload();
        IBufferLease outbound = NewFrame(Op, payload);
        IBufferLease original = outbound;
        FramePipeline.ProcessOutbound(
            ref outbound, true, 1, encrypt, s_key, encrypt ? 9u : null,
            encrypt ? CipherSuiteType.Chacha20Poly1305 : CipherSuiteType.None);

        Assert.False(outbound.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.COMPRESSED));

        IBufferLease inbound = outbound;
        FramePipeline.ProcessInbound(ref inbound, s_key, encrypt ? CipherSuiteType.Chacha20Poly1305 : CipherSuiteType.None, out _);
        Assert.Equal(payload, inbound.Span[FrameTransformer.Offset..].ToArray());

        if (!ReferenceEquals(inbound, outbound))
        {
            inbound.Dispose();
        }

        if (!ReferenceEquals(outbound, original))
        {
            outbound.Dispose();
        }

        original.Dispose();
    }

    private static void BackOff(ushort opCode)
    {
        for (int i = 0; i < AdaptiveCompression.MissesBeforeBackoff; i++)
        {
            AdaptiveCompression.Record(opCode, worthwhile: false);
        }

        Assert.Equal(AdaptiveCompression.MinSkipFrames, AdaptiveCompression.Interval(opCode));
    }

    /// <summary>Advances this thread's probe counter to just after an attempt, so the next frame is skipped.</summary>
    private static void AlignToNextProbe(ushort opCode)
    {
        int guard = AdaptiveCompression.MaxSkipFrames;
        while (!AdaptiveCompression.ShouldAttempt(opCode) && --guard > 0)
        {
        }

        Assert.True(guard > 0);
    }

    private static int CountAttempts(ushort opCode, int frames)
    {
        int attempts = 0;
        for (int i = 0; i < frames; i++)
        {
            if (AdaptiveCompression.ShouldAttempt(opCode))
            {
                attempts++;
            }
        }

        return attempts;
    }

    private static bool SendIsCompressed(ushort opCode, bool compressible, bool encrypt)
    {
        byte[] payload = compressible ? CompressiblePayload() : IncompressiblePayload();
        IBufferLease outbound = NewFrame(opCode, payload);
        IBufferLease original = outbound;
        try
        {
            FramePipeline.ProcessOutbound(
                ref outbound, true, 1, encrypt, s_key, encrypt ? 7u : null,
                encrypt ? CipherSuiteType.Chacha20Poly1305 : CipherSuiteType.None);
            return outbound.Span.AsHeaderRef().Flags.HasFlag(PacketFlags.COMPRESSED);
        }
        finally
        {
            if (!ReferenceEquals(outbound, original))
            {
                outbound.Dispose();
            }

            original.Dispose();
        }
    }

    private static BufferLease NewFrame(ushort opCode, byte[] payload)
    {
        BufferLease lease = BufferLease.Rent(FrameTransformer.Offset + payload.Length);
        lease.CommitLength(FrameTransformer.Offset + payload.Length);
        lease.Span[..FrameTransformer.Offset].Clear();
        lease.Span.AsHeaderRef() = new PacketHeader { OpCode = opCode, Flags = PacketFlags.NONE };
        payload.CopyTo(lease.Span[FrameTransformer.Offset..]);
        return lease;
    }

    private static byte[] IncompressiblePayload()
    {
        byte[] payload = new byte[1024];
        Csprng.NextBytes(payload);
        return payload;
    }

    private static byte[] CompressiblePayload()
    {
        byte[] payload = new byte[1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i / 32);
        }

        return payload;
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
}
