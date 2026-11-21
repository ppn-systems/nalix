using BenchmarkDotNet.Attributes;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Benchmarks.Shared;
using Nalix.Environment.Memory;
using Nalix.Framework.Memory.Buffers;

namespace Nalix.Network.Benchmarks.Datagrams;

/// <summary>
/// One message from client to server through both engines, until it is delivered and acknowledged:
/// framing, ChaCha20-Poly1305 per datagram, replay window, ACK ranges, reliability and fragmentation.
/// </summary>
[Config(typeof(NalixBenchmarkConfig))]
public class DatagramEngineBenchmarks
{
    private const int Batch = 64;

    private BufferPoolManager _buffers = null!;
    private DatagramPair _pair = null!;
    private byte[] _message = null!;

    [Params(DeliveryMethod.Unreliable, DeliveryMethod.ReliableOrdered)]
    public DeliveryMethod Delivery { get; set; }

    [Params(64, 1024, 16 * 1024)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // The pooled backend a host binds at startup (NetworkApplicationBuilder.Build).
        _buffers = new BufferPoolManager();
        BufferLease.ByteArrayPool.Configure(_buffers);

        _pair = new DatagramPair();
        // Zeros: random bytes can look like a fragment header, which TrySend refuses (real packets never do).
        _message = new byte[this.Size];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pair.Dispose();
        _buffers.Dispose();
    }

    /// <summary>A burst of <see cref="Batch"/> messages, pumped until delivered and acknowledged (time per message).</summary>
    [Benchmark(OperationsPerInvoke = Batch)]
    public long SendDeliverAck()
    {
        // Unreliable messages are not fragmented: skip the combination the engine refuses.
        if (this.Delivery == DeliveryMethod.Unreliable && this.Size > 1024)
        {
            return 0;
        }

        for (int i = 0; i < Batch; i++)
        {
            if (!_pair.Client.TrySend(_message, this.Delivery, 0, 0, _pair.Now))
            {
                throw new InvalidOperationException("TrySend refused the message.");
            }
        }

        _pair.Pump(Batch);
        return _pair.ServerHandler.Messages;
    }
}
