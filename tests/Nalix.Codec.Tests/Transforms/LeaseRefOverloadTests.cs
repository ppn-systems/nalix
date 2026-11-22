// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Transforms;

#pragma warning disable CS0618 // These tests cover the obsolete IBufferLease source overloads until they are removed.

/// <summary>
/// The <see cref="IBufferLease"/> overloads forward to the <see cref="LeaseRef"/> ones. A missing source
/// must behave as it did before the split: the throwing methods throw, the Try methods return false.
/// </summary>
public sealed class LeaseRefOverloadTests
{
    private const CipherSuiteType Suite = CipherSuiteType.Chacha20Poly1305;

    private static readonly byte[] s_key = new byte[32];

    [Fact]
    public void ThrowingOverloads_WhenSourceLeaseIsNull_ThrowArgumentNullException()
    {
        using BufferLease dest = BufferLease.Rent(64);

        _ = Assert.Throws<ArgumentNullException>(() => FrameCompression.CompressFrame((IBufferLease)null!));
        _ = Assert.Throws<ArgumentNullException>(() => FrameCompression.DecompressFrame((IBufferLease)null!));
        _ = Assert.Throws<ArgumentNullException>(() => FrameCipher.EncryptFrame((IBufferLease)null!, s_key, null, Suite));
        _ = Assert.Throws<ArgumentNullException>(() => FrameCipher.DecryptFrame((IBufferLease)null!, s_key, Suite, out _));
        _ = Assert.Throws<ArgumentNullException>(() => FrameTransformer.Compress((IBufferLease)null!, dest));
        _ = Assert.Throws<ArgumentNullException>(() => FrameTransformer.Decompress((IBufferLease)null!, dest));
        _ = Assert.Throws<ArgumentNullException>(() => FrameTransformer.Encrypt((IBufferLease)null!, dest, s_key, null, Suite));
        _ = Assert.Throws<ArgumentNullException>(() => FrameTransformer.Decrypt((IBufferLease)null!, dest, s_key, Suite, out _));
    }

    [Fact]
    public void TryOverloads_WhenSourceLeaseIsNull_ReturnFalse()
    {
        using BufferLease dest = BufferLease.Rent(64);

        Assert.False(FrameCompression.TryDecompressFrame((IBufferLease)null!, out IBufferLease? decompressed));
        Assert.Null(decompressed);

        Assert.False(FrameCipher.TryDecryptFrame((IBufferLease)null!, s_key, Suite, out uint frameSeq, out IBufferLease? decrypted));
        Assert.Null(decrypted);
        Assert.Equal(0u, frameSeq);

        Assert.False(FrameTransformer.TryDecompress((IBufferLease)null!, dest));
        Assert.False(FrameTransformer.TryDecrypt((IBufferLease)null!, dest, s_key, Suite, out uint seq));
        Assert.Equal(0u, seq);
    }

    [Fact]
    public void TryOverloads_WhenSourceIsTheDefaultLeaseRef_ReturnFalse()
    {
        using BufferLease dest = BufferLease.Rent(64);

        Assert.False(FrameCompression.TryDecompressFrame(default(LeaseRef), out IBufferLease? decompressed));
        Assert.Null(decompressed);

        Assert.False(FrameCipher.TryDecryptFrame(default(LeaseRef), s_key, Suite, out _, out IBufferLease? decrypted));
        Assert.Null(decrypted);

        Assert.False(FrameTransformer.TryDecompress(default(LeaseRef), dest));
        Assert.False(FrameTransformer.TryDecrypt(default(LeaseRef), dest, s_key, Suite, out _));
    }

    [Fact]
    public void TryOverloads_WhenDestinationIsNullOrKeyIsEmpty_ReturnFalse()
    {
        using BufferLease src = BufferLease.Rent(64);
        src.CommitLength(64);
        using BufferLease dest = BufferLease.Rent(64);

        Assert.False(FrameTransformer.TryDecompress(src, null!));
        Assert.False(FrameTransformer.TryDecompress(src.AsRef(), null!));
        Assert.False(FrameTransformer.TryDecrypt(src, null!, s_key, Suite, out _));
        Assert.False(FrameTransformer.TryDecrypt(src.AsRef(), dest, [], Suite, out _));
    }
}
