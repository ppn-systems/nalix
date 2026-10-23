using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Transforms;
using Nalix.Environment.Extensions;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.DataFrames;

public sealed class DataFramesSignalAndTransformEdgeTests
{
    [Fact]
    public void FragmentAssemblerWhenTotalChunksChangesEvictsStreamAndThrows()
    {
        using FragmentAssembler assembler = new();
        _ = assembler.Add(new FragmentHeader(50, 0, 2, false), [1], out _);

        bool evicted = false;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            assembler.Add(new FragmentHeader(50, 1, 3, true), [2], out evicted));

        Assert.True(evicted);
        Assert.Contains("changed TotalChunks", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void FragmentAssemblerWhenSecondChunkIsOutOfOrderThrowsAndEvictsCorruptedStream()
    {
        using FragmentAssembler assembler = new();
        _ = assembler.Add(new FragmentHeader(51, 0, 3, false), [1], out _);

        bool evicted = false;
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            assembler.Add(new FragmentHeader(51, 2, 3, true), [2], out evicted));

        Assert.True(evicted);
        Assert.Contains("out-of-order", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, assembler.OpenStreamCount);
    }

    [Fact]
    public void FragmentAssemblerWhenMaxOpenStreamsReachedReturnsNullAndMarksEvicted()
    {
        using FragmentAssembler assembler = new() { MaxOpenStreams = 1 };
        _ = assembler.Add(new FragmentHeader(60, 0, 2, false), [1], out _);

        FragmentAssemblyResult? result = assembler.Add(new FragmentHeader(61, 0, 2, false), [2], out bool evicted);

        Assert.Null(result);
        Assert.True(evicted);
        Assert.Equal(1, assembler.OpenStreamCount);
    }


    [Fact]
    public void SessionResumeInitializeWhenOptionalValuesAreOmittedUsesExpectedDefaults()
    {
        SessionResume packet = new();
        packet.Initialize(SessionResumeStage.REQUEST, 0UL);

        Assert.Equal((ushort)ProtocolOpCode.SESSION_SIGNAL, packet.Header.OpCode);
        Assert.Equal(SessionResumeStage.REQUEST, packet.Stage);
        Assert.Equal(ProtocolReason.NONE, packet.Reason);
        Assert.True(packet.Proof.IsZero);
        Assert.Equal(PacketPriority.URGENT, packet.Header.Priority);
    }

    [Fact]
    public void ControlInitializeOverloadWithoutOpcodeUpdatesCoreProperties()
    {
        Control packet = new();
        packet.Initialize(ControlType.PING, sequenceId: 44, flags: PacketFlags.SYSTEM, reasonCode: ProtocolReason.TIMEOUT);

        Assert.Equal(ControlType.PING, packet.Type);
        Assert.Equal(44u, packet.Header.SequenceId);
        Assert.Equal(ProtocolReason.TIMEOUT, packet.Reason);
    }

    [Fact]
    public void DirectiveInitializeOverloadWithoutOpcodeKeepsDirectiveOpcode()
    {
        Directive packet = new();
        packet.Initialize(ControlType.REDIRECT, ProtocolReason.THROTTLED, ProtocolAdvice.SLOW_DOWN, sequenceId: 9, controlFlags: ControlFlags.SLOW_DOWN, arg0: 1, arg1: 2, arg2: 3);

        Assert.Equal((ushort)ProtocolOpCode.SYSTEM_DIRECTIVE, packet.Header.OpCode);
        Assert.Equal(ControlType.REDIRECT, packet.Type);
        Assert.Equal(ProtocolReason.THROTTLED, packet.Reason);
        Assert.Equal(ProtocolAdvice.SLOW_DOWN, packet.Action);
        Assert.Equal(ControlFlags.SLOW_DOWN, packet.Control);
        Assert.Equal(PacketPriority.HIGH, packet.Header.Priority);
    }

    [Fact]
    public void FrameCipherWhenSourceIsNullThrowsArgumentNullException()
    {
        byte[] key = new byte[32];
        _ = Assert.Throws<ArgumentNullException>(() => FrameCipher.EncryptFrame(null!, key, null, CipherSuiteType.Chacha20Poly1305));
        _ = Assert.Throws<ArgumentNullException>(() => FrameCipher.DecryptFrame(null!, key, CipherSuiteType.Chacha20Poly1305, out _));
    }

    [Fact]
    public void FrameCompressionWhenSourceIsNullThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => FrameCompression.CompressFrame(null!));
        _ = Assert.Throws<ArgumentNullException>(() => FrameCompression.DecompressFrame(null!));
    }

    [Fact]
    public void FrameCipherDecryptRemovesOnlyEncryptedFlagAndKeepsOtherFlags()
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
    public void FrameCompressionDecompressRemovesOnlyCompressedFlagAndKeepsOtherFlags()
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

    [Fact]
    public void FrameCipherDecryptWhenCiphertextFrameIsTooShortThrowsCipherException()
    {
        byte[] key = new byte[32];
        using BufferLease shortFrame = BufferLease.Rent(FrameTransformer.Offset);
        shortFrame.CommitLength(FrameTransformer.Offset);

        _ = Assert.ThrowsAny<CipherException>(() => FrameCipher.DecryptFrame(shortFrame, key, CipherSuiteType.Chacha20, out _));
    }
}




















