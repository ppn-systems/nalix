using System;
using BenchmarkDotNet.Attributes;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Benchmarks.Shared;
using Nalix.Codec.Transforms;
using Nalix.Environment.Extensions;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Benchmarks.Transforms;

[Config(typeof(NalixBenchmarkConfig))]
public class FrameTransformerBenchmarks
{
    private static readonly byte[] s_testKey = new byte[32];

    [Params(64, 1024)]
    public int PayloadSize;

    private byte[] _payload = null!;

    static FrameTransformerBenchmarks()
    {
        Random.Shared.NextBytes(s_testKey);
    }

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadSize];
        Random.Shared.NextBytes(_payload);
    }

    private IBufferLease CreateSourceLease()
    {
        var lease = BufferLease.Rent(FrameTransformer.Offset + PayloadSize);
        lease.CommitLength(FrameTransformer.Offset + PayloadSize);
        lease.Span[..FrameTransformer.Offset].Clear();
        lease.Span.AsHeaderRef() = new PacketHeader { Flags = PacketFlags.NONE };
        _payload.CopyTo(lease.Span[FrameTransformer.Offset..]);
        return lease;
    }

    // ── AEAD (ChaCha20Poly1305) ──

    [Benchmark]
    public void Encrypt_AEAD_ChaCha20Poly1305()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src.AsRef(), s_testKey, null, CipherSuiteType.Chacha20Poly1305);
    }

    [Benchmark]
    public void Decrypt_AEAD_ChaCha20Poly1305()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src.AsRef(), s_testKey, null, CipherSuiteType.Chacha20Poly1305);
        using IBufferLease decrypted = FrameCipher.DecryptFrame(encrypted.AsRef(), s_testKey, CipherSuiteType.Chacha20Poly1305, out _);
    }

    // ── AEAD (Salsa20Poly1305) ──

    [Benchmark]
    public void Encrypt_AEAD_Salsa20Poly1305()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src.AsRef(), s_testKey, null, CipherSuiteType.Salsa20Poly1305);
    }

    [Benchmark]
    public void Decrypt_AEAD_Salsa20Poly1305()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src.AsRef(), s_testKey, null, CipherSuiteType.Salsa20Poly1305);
        using IBufferLease decrypted = FrameCipher.DecryptFrame(encrypted.AsRef(), s_testKey, CipherSuiteType.Salsa20Poly1305, out _);
    }

    // ── Symmetric Stream (ChaCha20) ──

    [Benchmark]
    public void Encrypt_Symmetric_ChaCha20()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src.AsRef(), s_testKey, null, CipherSuiteType.Chacha20);
    }

    [Benchmark]
    public void Decrypt_Symmetric_ChaCha20()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src.AsRef(), s_testKey, null, CipherSuiteType.Chacha20);
        using IBufferLease decrypted = FrameCipher.DecryptFrame(encrypted.AsRef(), s_testKey, CipherSuiteType.Chacha20, out _);
    }

    // ── Symmetric Stream (Salsa20) ──

    [Benchmark]
    public void Encrypt_Symmetric_Salsa20()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src.AsRef(), s_testKey, null, CipherSuiteType.Salsa20);
    }

    [Benchmark]
    public void Decrypt_Symmetric_Salsa20()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease encrypted = FrameCipher.EncryptFrame(src.AsRef(), s_testKey, null, CipherSuiteType.Salsa20);
        using IBufferLease decrypted = FrameCipher.DecryptFrame(encrypted.AsRef(), s_testKey, CipherSuiteType.Salsa20, out _);
    }

    // ── LZ4 Compression ──

    [Benchmark]
    public void Compress_LZ4()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease compressed = FrameCompression.CompressFrame(src.AsRef());
    }

    [Benchmark]
    public void Decompress_LZ4()
    {
        using IBufferLease src = CreateSourceLease();
        using IBufferLease compressed = FrameCompression.CompressFrame(src.AsRef());
        using IBufferLease decompressed = FrameCompression.DecompressFrame(compressed.AsRef());
    }
}
