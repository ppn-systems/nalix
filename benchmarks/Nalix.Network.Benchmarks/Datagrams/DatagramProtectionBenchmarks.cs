using BenchmarkDotNet.Attributes;
using Nalix.Abstractions.Primitives;
using Nalix.Benchmarks.Shared;
using Nalix.Codec.Security.Datagram;

namespace Nalix.Network.Benchmarks.Datagrams;

/// <summary>ChaCha20-Poly1305 per datagram, as the engine calls it (13-byte header as AAD).</summary>
[Config(typeof(NalixBenchmarkConfig))]
public class DatagramProtectionBenchmarks
{
    private ChaCha20Poly1305DatagramProtection _client = null!;
    private ChaCha20Poly1305DatagramProtection _server = null!;
    private byte[] _header = null!;
    private byte[] _plaintext = null!;
    private byte[] _sealed = null!;
    private byte[] _opened = null!;
    private ulong _packetNumber;

    [Params(64, 1200)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Bytes32 secret = new(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
        byte[] nonce = new byte[16];
        _client = ChaCha20Poly1305DatagramProtection.Create(secret, 7, nonce, isServer: false);
        _server = ChaCha20Poly1305DatagramProtection.Create(secret, 7, nonce, isServer: true);
        _header = new byte[13];
        _plaintext = new byte[this.Size];
        _sealed = new byte[this.Size + _client.TagSize];
        _opened = new byte[this.Size];
        _client.Seal(1, _header, _plaintext, _sealed);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Benchmark]
    public void Seal() => _client.Seal(++_packetNumber, _header, _plaintext, _sealed);

    [Benchmark]
    public bool Open() => _server.TryOpen(1, false, _header, _sealed, _opened);
}
