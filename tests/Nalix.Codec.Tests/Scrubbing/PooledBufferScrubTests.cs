// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Nalix.Abstractions;
using Nalix.Abstractions.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Memory;
using Xunit;

namespace Nalix.Codec.Tests.Scrubbing;

/// <summary>
/// Since #370 the byte-array pool no longer clears arrays on return, so every owner of a buffer
/// that held plaintext must scrub it. These tests install a spy pool that inspects each array as
/// it is returned and records any array that still contains a known plaintext marker.
/// </summary>
[Collection(PooledBufferScrubCollection.Name)]
public sealed class PooledBufferScrubTests : IDisposable
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("TOP-SECRET-PLAINTEXT-MARKER-7f3a");

    private readonly ScanningPool _pool = new(Marker);

    public PooledBufferScrubTests() => BufferLease.ByteArrayPool.Configure(_pool);

    public void Dispose() => BufferLease.ByteArrayPool.Configure(new SharedPool());

    [Fact]
    public void ProcessOutbound_FusedPathThrowsAfterCompress_ScrubsCompressedPlaintext()
    {
        IBufferLease current = NewPlaintextFrame(repeat: 64);

        // A 16-byte key is rejected by the AEAD after LZ4 has already written the compressed
        // plaintext into the scratch region of the output lease.
        byte[] badKey = new byte[16];
        Action act = () => FramePipeline.ProcessOutbound(
            ref current, enableCompress: true, minSizeToCompress: 1, enableEncrypt: true,
            badKey, seq: 1, CipherSuiteType.Chacha20Poly1305);

        Assert.ThrowsAny<Exception>(act);
        current.Dispose();

        Assert.Empty(_pool.Leaks);
    }

    [Fact]
    public void ProcessOutbound_FusedPathSucceeds_ReturnsNoPlaintextToPool()
    {
        IBufferLease original = NewPlaintextFrame(repeat: 64);
        IBufferLease current = original;

        FramePipeline.ProcessOutbound(
            ref current, enableCompress: true, minSizeToCompress: 1, enableEncrypt: true,
            NewKey(), seq: 1, CipherSuiteType.Chacha20Poly1305);

        Assert.NotSame(original, current);
        current.Dispose();
        original.Dispose();

        Assert.Empty(_pool.Leaks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_EncryptedFrame_NoPlaintextReachesPool(bool compress)
    {
        byte[] key = NewKey();
        IBufferLease plain = NewPlaintextFrame(repeat: 64);
        IBufferLease wire = plain;

        FramePipeline.ProcessOutbound(
            ref wire, enableCompress: compress, minSizeToCompress: 1, enableEncrypt: true,
            key, seq: 7, CipherSuiteType.Chacha20Poly1305);
        plain.Dispose();

        IBufferLease inbound = wire;
        FramePipeline.ProcessInbound(ref inbound, key, CipherSuiteType.Chacha20Poly1305, out uint? seq);

        Assert.Equal(7u, seq);
        Assert.True(inbound.Span.IndexOf(Marker) >= 0, "decrypted frame should contain the plaintext");

        inbound.Dispose();
        wire.Dispose();

        Assert.Empty(_pool.Leaks);
    }

    [Fact]
    public void TryProcessInbound_TamperedCiphertext_ReturnsNoPlaintextToPool()
    {
        byte[] key = NewKey();
        IBufferLease plain = NewPlaintextFrame(repeat: 64);
        IBufferLease wire = plain;

        FramePipeline.ProcessOutbound(
            ref wire, enableCompress: true, minSizeToCompress: 1, enableEncrypt: true,
            key, seq: 3, CipherSuiteType.Chacha20Poly1305);
        plain.Dispose();

        // Flip the last byte (inside the authentication tag).
        byte[]? raw = null;
        Assert.True(wire.ReleaseOwnership(out raw, out int start, out int length));
        raw![start + length - 1] ^= 0xFF;
        IBufferLease tampered = BufferLease.TakeOwnership(raw, start, length);

        IBufferLease inbound = tampered;
        Assert.False(FramePipeline.TryProcessInbound(ref inbound, key, CipherSuiteType.Chacha20Poly1305, out _));
        Assert.Same(tampered, inbound);

        tampered.Dispose();
        wire.Dispose();

        Assert.Empty(_pool.Leaks);
    }

    private static byte[] NewKey()
    {
        byte[] key = new byte[32];
        new Random(1234).NextBytes(key);
        return key;
    }

    private static BufferLease NewPlaintextFrame(int repeat)
    {
        int payload = Marker.Length * repeat;
        BufferLease lease = BufferLease.Rent(FrameTransformer.Offset + payload);
        Span<byte> span = lease.SpanFull;
        span[..FrameTransformer.Offset].Clear();
        for (int i = 0; i < repeat; i++)
        {
            Marker.CopyTo(span[(FrameTransformer.Offset + (i * Marker.Length))..]);
        }

        lease.CommitLength(FrameTransformer.Offset + payload);
        return lease;
    }

    private sealed class ScanningPool(byte[] marker) : IBufferPoolManager
    {
        public ConcurrentBag<int> Leaks { get; } = [];

        public byte[] Rent(int minimumLength = 256) => ArrayPool<byte>.Shared.Rent(minimumLength);

        public void Return(byte[]? array, bool arrayClear = false)
        {
            if (array is null)
            {
                return;
            }

            if (!arrayClear && array.AsSpan().IndexOf(marker.AsSpan(0, 16)) >= 0)
            {
                this.Leaks.Add(array.Length);
            }

            ArrayPool<byte>.Shared.Return(array, arrayClear);
        }

        public string GenerateReport() => string.Empty;

        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer) { }
    }

    private sealed class SharedPool : IBufferPoolManager
    {
        public byte[] Rent(int minimumLength = 256) => ArrayPool<byte>.Shared.Rent(minimumLength);

        public void Return(byte[]? array, bool arrayClear = false)
        {
            if (array is not null)
            {
                ArrayPool<byte>.Shared.Return(array, arrayClear);
            }
        }

        public string GenerateReport() => string.Empty;

        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer) { }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PooledBufferScrubCollection
{
    public const string Name = "PooledBufferScrub";
}
