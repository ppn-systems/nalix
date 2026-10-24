// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Codec.LZ4;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Transforms;
using Nalix.Environment.Extensions;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Transforms;

/// <summary>
/// Adversarial tests for the real serialize -> LZ4-compress -> LZ4-decompress -> deserialize
/// wire pipeline (<see cref="FrameCompression"/> over <see cref="PacketBase{TSelf}"/> packets).
/// Goal: value-exact round-trips plus clean rejection when any stage-boundary buffer is corrupted.
/// </summary>
public sealed class FramePipelineAdversarialTests
{
    private const int Seed = 424242;

    // ---- Helpers: wrap a serialized packet's bytes into a BufferLease honoring FrameTransformer.Offset ----

    private static BufferLease ToLease(byte[] serialized)
    {
        BufferLease lease = BufferLease.Rent(serialized.Length);
        serialized.CopyTo(lease.SpanFull);
        lease.CommitLength(serialized.Length);
        return lease;
    }

    private static Control MakeControl(System.Random rng)
    {
        Control p = new();
        p.Initialize(
            (ControlType)rng.Next(0, 5),
            (ushort)rng.Next(0, ushort.MaxValue + 1),
            PacketFlags.SYSTEM,
            (ProtocolReason)rng.Next(0, 5));
        return p;
    }

    private static Directive MakeDirective(System.Random rng)
    {
        Directive p = new();
        p.Initialize(
            (ControlType)rng.Next(0, 5),
            (ProtocolReason)rng.Next(0, 5),
            (ProtocolAdvice)rng.Next(0, 3),
            (ushort)rng.Next(0, ushort.MaxValue + 1),
            PacketFlags.SYSTEM,
            (ControlFlags)rng.Next(0, 3),
            (uint)rng.Next(),
            (uint)rng.Next(),
            (ushort)rng.Next(0, ushort.MaxValue + 1));
        return p;
    }

    private static SessionResume MakeSessionResume(System.Random rng)
    {
        byte[] proofBytes = new byte[32];
        rng.NextBytes(proofBytes);
        SessionResume p = new();
        p.Initialize(
            rng.Next(0, 2) == 0 ? SessionResumeStage.REQUEST : SessionResumeStage.RESPONSE,
            (ulong)rng.NextInt64(),
            (ProtocolReason)rng.Next(0, 5),
            new Bytes32(proofBytes));
        return p;
    }

    // ---- Full round-trip: serialize -> compress -> decompress -> deserialize, value-exact ----

    [Fact]
    public void RoundTrip_WhenControlIsCompressedAndDecompressed_IsValueExact()
    {
        System.Random rng = new(Seed);
        for (int i = 0; i < 200; i++)
        {
            Control original = MakeControl(rng);
            byte[] serialized = original.Serialize();

            using BufferLease src = ToLease(serialized);
            using IBufferLease compressed = FrameCompression.CompressFrame(src);
            using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed);

            byte[] roundTripped = decompressed.Span.ToArray();
            Control restored = Control.Deserialize(roundTripped);

            Assert.True(restored is not null, $"iteration {i}, seed {Seed}: deserialize returned null");
            Assert.Equal(original.Header.OpCode, restored.Header.OpCode);
            Assert.Equal(original.Type, restored.Type);
            Assert.Equal(original.Reason, restored.Reason);
            Assert.Equal(original.Header.SequenceId, restored.Header.SequenceId);
        }
    }

    [Fact]
    public void RoundTrip_WhenDirectiveIsCompressedAndDecompressed_IsValueExact()
    {
        System.Random rng = new(Seed + 1);
        for (int i = 0; i < 200; i++)
        {
            Directive original = MakeDirective(rng);
            byte[] serialized = original.Serialize();

            using BufferLease src = ToLease(serialized);
            using IBufferLease compressed = FrameCompression.CompressFrame(src);
            using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed);

            byte[] roundTripped = decompressed.Span.ToArray();
            Directive restored = Directive.Deserialize(roundTripped);

            Assert.True(restored is not null, $"iteration {i}, seed {Seed + 1}: deserialize returned null");
            Assert.Equal(original.Type, restored.Type);
            Assert.Equal(original.Reason, restored.Reason);
            Assert.Equal(original.Action, restored.Action);
            Assert.Equal(original.Control, restored.Control);
            Assert.Equal(original.Arg0, restored.Arg0);
            Assert.Equal(original.Arg1, restored.Arg1);
        }
    }

    [Fact]
    public void RoundTrip_WhenSessionResumeIsCompressedAndDecompressed_IsValueExact()
    {
        System.Random rng = new(Seed + 2);
        for (int i = 0; i < 200; i++)
        {
            SessionResume original = MakeSessionResume(rng);
            byte[] serialized = original.Serialize();

            using BufferLease src = ToLease(serialized);
            using IBufferLease compressed = FrameCompression.CompressFrame(src);
            using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed);

            byte[] roundTripped = decompressed.Span.ToArray();
            SessionResume restored = SessionResume.Deserialize(roundTripped);

            Assert.True(restored is not null, $"iteration {i}, seed {Seed + 2}: deserialize returned null");
            Assert.Equal(original.Stage, restored.Stage);
            Assert.Equal(original.SessionToken, restored.SessionToken);
            Assert.Equal(original.Reason, restored.Reason);
            Assert.Equal(original.Proof, restored.Proof);
        }
    }

    // ---- Stage-boundary tampering: flip one byte, expect clean rejection (never silent corruption / crash) ----

    [Fact]
    public void Tamper_WhenByteIsFlippedBeforeCompression_RejectsCleanlyOrChangesValue()
    {
        System.Random rng = new(Seed + 3);
        Control original = MakeControl(rng);
        byte[] serialized = original.Serialize();

        // Flip each byte of the serialized (pre-compress) buffer; compress+decompress+deserialize must
        // either throw a documented exception or produce a value that differs from the original -
        // it must never silently reproduce the original value nor crash the process.
        for (int i = 0; i < serialized.Length; i++)
        {
            byte[] tampered = (byte[])serialized.Clone();
            tampered[i] ^= 0xFF;

            using BufferLease src = ToLease(tampered);

            try
            {
                using IBufferLease compressed = FrameCompression.CompressFrame(src);
                using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed);
                byte[] roundTripped = decompressed.Span.ToArray();
                Control? restored = Control.Deserialize(roundTripped);

                if (restored is not null)
                {
                    bool identical = roundTripped.AsSpan().SequenceEqual(serialized);

                    Assert.False(identical,
                        $"byte {i}/{serialized.Length} flipped (seed {Seed + 3}) but pipeline silently reproduced the original bytes.");
                }
            }
            catch (System.Exception ex) when (ex is LZ4Exception or SerializationFailureException)
            {
                // Documented, acceptable rejection.
            }
        }
    }

    [Fact]
    public void Tamper_WhenByteIsFlippedAfterCompression_RejectsCleanlyOrChangesValue()
    {
        System.Random rng = new(Seed + 4);
        Directive original = MakeDirective(rng);
        byte[] serialized = original.Serialize();

        using BufferLease src = ToLease(serialized);
        using IBufferLease compressed = FrameCompression.CompressFrame(src);
        byte[] compressedBytes = compressed.Span.ToArray();

        for (int i = 0; i < compressedBytes.Length; i++)
        {
            byte[] tampered = (byte[])compressedBytes.Clone();
            tampered[i] ^= 0xFF;

            using BufferLease tamperedLease = ToLease(tampered);

            try
            {
                using IBufferLease decompressed = FrameCompression.DecompressFrame(tamperedLease);
                byte[] roundTripped = decompressed.Span.ToArray();
                Directive? restored = Directive.Deserialize(roundTripped);

                if (restored is not null)
                {
                    bool identical = roundTripped.AsSpan().SequenceEqual(serialized);

                    Assert.False(identical,
                        $"byte {i}/{compressedBytes.Length} flipped in compressed buffer (seed {Seed + 4}) " +
                        "but pipeline silently reproduced the original bytes.");
                }
            }
            catch (System.Exception ex) when (ex is LZ4Exception or SerializationFailureException)
            {
                // Documented, acceptable rejection.
            }
        }
    }

    [Fact]
    public void Tamper_WhenByteIsFlippedAfterDecompression_RejectsCleanlyOrChangesValue()
    {
        System.Random rng = new(Seed + 5);
        SessionResume original = MakeSessionResume(rng);
        byte[] serialized = original.Serialize();

        using BufferLease src = ToLease(serialized);
        using IBufferLease compressed = FrameCompression.CompressFrame(src);
        using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed);
        byte[] decompressedBytes = decompressed.Span.ToArray();

        for (int i = 0; i < decompressedBytes.Length; i++)
        {
            byte[] tampered = (byte[])decompressedBytes.Clone();
            tampered[i] ^= 0xFF;

            SessionResume? restored = null;
            try
            {
                restored = SessionResume.Deserialize(tampered);
            }
            catch (System.Exception ex) when (ex is LZ4Exception or SerializationFailureException)
            {
                // Documented, acceptable rejection.
                continue;
            }

            if (restored is not null)
            {
                bool identical = restored.Serialize().AsSpan().SequenceEqual(decompressedBytes);

                Assert.False(identical,
                    $"byte {i}/{decompressedBytes.Length} flipped post-decompress (seed {Seed + 5}) " +
                    "but deserialize silently reproduced the original bytes.");
            }
        }
    }

    // ---- Allocation-bomb & header consistency defenses in FrameTransformer / FrameCompression / FramePipeline ----

    [Fact]
    public void TryProcessInbound_WhenFrameIsShortOrCarriesBombHeader_ReturnsFalseWithoutThrowing()
    {
        // 1. Frame shorter than PacketHeader.Size (6 bytes) must return false without throwing ArgumentException.
        using BufferLease shortLease = BufferLease.Rent(4);
        shortLease.CommitLength(3);
        IBufferLease current = shortLease;
        Assert.False(FramePipeline.TryProcessInbound(ref current, default, Nalix.Abstractions.Security.CipherSuiteType.None, out _));

        // 2. Unencrypted compressed frame with fake 16 MB OriginalLength must be rejected cleanly.
        byte[] bombFrame = new byte[FrameTransformer.Offset + LZ4BlockHeader.Size + 4];
        bombFrame.AsSpan().AsHeaderRef() = new PacketHeader { Flags = PacketFlags.COMPRESSED };
        System.BitConverter.GetBytes(16 * 1024 * 1024).CopyTo(bombFrame, FrameTransformer.Offset);
        System.BitConverter.GetBytes(LZ4BlockHeader.Size + 4).CopyTo(bombFrame, FrameTransformer.Offset + 4);

        using BufferLease bombLease = ToLease(bombFrame);
        IBufferLease inboundBomb = bombLease;
        Assert.False(FramePipeline.TryProcessInbound(ref inboundBomb, default, Nalix.Abstractions.Security.CipherSuiteType.None, out _));
        Assert.Same(bombLease, inboundBomb);
    }
}
