// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Transforms;

/// <summary>
/// The frame transforms take their source as a <see cref="LeaseRef"/>. A source that refers to no lease,
/// a missing destination or an empty key must make the Try methods return false instead of throwing.
/// </summary>
public sealed class LeaseRefOverloadTests
{
    private const CipherSuiteType Suite = CipherSuiteType.Chacha20Poly1305;

    private static readonly byte[] s_key = new byte[32];

    [Fact]
    public void TryMethods_WhenSourceIsTheDefaultLeaseRef_ReturnFalse()
    {
        using BufferLease dest = BufferLease.Rent(64);

        Assert.False(FrameCompression.TryDecompressFrame(default, out IBufferLease? decompressed));
        Assert.Null(decompressed);

        Assert.False(FrameCipher.TryDecryptFrame(default, s_key, Suite, out uint frameSeq, out IBufferLease? decrypted));
        Assert.Null(decrypted);
        Assert.Equal(0u, frameSeq);

        Assert.False(FrameTransformer.TryDecompress(default, dest));
        Assert.False(FrameTransformer.TryDecrypt(default, dest, s_key, Suite, out uint seq));
        Assert.Equal(0u, seq);
    }

    [Fact]
    public void TryMethods_WhenDestinationIsNullOrKeyIsEmpty_ReturnFalse()
    {
        using BufferLease src = BufferLease.Rent(64);
        src.CommitLength(64);
        using BufferLease dest = BufferLease.Rent(64);

        Assert.False(FrameTransformer.TryDecompress(src.AsRef(), null!));
        Assert.False(FrameTransformer.TryDecrypt(src.AsRef(), null!, s_key, Suite, out _));
        Assert.False(FrameTransformer.TryDecrypt(src.AsRef(), dest, [], Suite, out _));
    }

    [Fact]
    public void ThrowingMethods_WhenSourceIsTheDefaultLeaseRef_ThrowInvalidOperationException()
    {
        _ = Assert.Throws<InvalidOperationException>(() => FrameCompression.CompressFrame(default));
        _ = Assert.Throws<InvalidOperationException>(() => FrameCompression.DecompressFrame(default));
        _ = Assert.Throws<InvalidOperationException>(() => FrameCipher.EncryptFrame(default, s_key, null, Suite));
        _ = Assert.Throws<InvalidOperationException>(() => FrameCipher.DecryptFrame(default, s_key, Suite, out _));
    }
}
