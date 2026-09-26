using System;
using BenchmarkDotNet.Attributes;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Identity;
using Nalix.Environment.Configuration;
using Nalix.Runtime.Options;
using Nalix.Runtime.Throttling;
using Nalix.Benchmarks.Shared;

namespace Nalix.Runtime.Benchmarks.Throttling;

[Config(typeof(NalixBenchmarkConfig))]
public class PolicyRateLimiterBenchmarks
{
    private PolicyRateLimiter _limiter = null!;
    private BenchmarkConnection _connection = null!;
    private IPacketContext<IPacket> _context = null!;

    private class BenchmarkNetworkEndpoint : INetworkEndpoint
    {
        public string Address => "127.0.0.1";
        public int Port => 80;
        public bool HasPort => true;
        public bool IsIPv6 => false;

        public override int GetHashCode() => Address.GetHashCode(StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is BenchmarkNetworkEndpoint other && Address == other.Address;
    }

    /// <summary>Minimal op-code reader; the rate limiter never inspects a payload.</summary>
    private sealed class StubOpCodeExtractor : Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private class BenchmarkConnection : IConnection
    {
        public bool IsDisposed => false;
        public bool IsUdpCreated => false;
        public ulong ConnectionId => 0;
        public string? UserId { get; set; }
        public long UpTime => 0;
        public long BytesSent => 0;
        public long BytesReceived => 0;
        public long LastPingTime => 0;
        public INetworkEndpoint NetworkEndpoint { get; } = new BenchmarkNetworkEndpoint();
        public IObjectMap<AttributeKey, object> Attributes => null!;

        public bool ExcludeFromIdleTimeout { get; set; }

        public Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor PacketClassifier { get; } =
            new StubOpCodeExtractor();

        public void UpdateIdleTimeout(int newTimeoutMs) { }

        private System.Collections.Concurrent.ConcurrentDictionary<ushort, object>? _rateLimitCache;
        public System.Collections.Concurrent.ConcurrentDictionary<ushort, object> RateLimitCache => _rateLimitCache ??= new();

        public Bytes32 Secret { get; set; }
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }

#pragma warning disable CS0067 // Event is never used
        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;
#pragma warning restore CS0067

        public void Disconnect(string? reason = null) { }
        public void Dispose() { }

        public int ErrorCount => 0;
        public void IncrementErrorCount() { }

        public IConnection.ITransport TCP => null!;
        public IConnection.ITransport UDP => null!;
    }

    private class BenchmarkPacket : IPacket
    {
        public int Length => 10;
        public PacketHeader Header { get; set; }

        public BenchmarkPacket(ushort opCode)
        {
            Header = new PacketHeader { OpCode = opCode };
        }

        public byte[] Serialize() => Array.Empty<byte>();
        public int Serialize(Span<byte> buffer) => 0;
    }

    private class BenchmarkPacketContext : IPacketContext<IPacket>
    {
        public bool IsReliable => true;
        public bool EncryptedOnWire => false;
        public bool SkipOutbound => false;
        public IPacket Packet { get; }
        public IConnection Connection { get; }
        public PacketMetadata Attributes { get; }
        public IPacketSender Sender => null!;
        public Nalix.Abstractions.Injection.IPacketScope Scope => null!;
        public System.Threading.CancellationToken CancellationToken => System.Threading.CancellationToken.None;

        public BenchmarkPacketContext(IConnection connection, PacketRateLimitAttribute rateLimit)
        {
            Connection = connection;
            Packet = new BenchmarkPacket(0x1010);
            Attributes = new PacketMetadata(
                new PacketOpcodeAttribute(0x1010),
                timeout: null,
                permission: null,
                encryption: null,
                rateLimit: rateLimit,
                transport: null);
        }

        public void ResetForPool() { }
    }

    [GlobalSetup]
    public void Setup()
    {
        _limiter = new PolicyRateLimiter();
        _connection = new BenchmarkConnection();
        var rateLimit = new PacketRateLimitAttribute(1000000, 1000000);
        _context = new BenchmarkPacketContext(_connection, rateLimit);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _limiter.Dispose();
        _connection.Dispose();
    }

    [Benchmark]
    public TokenBucketLimiter.RateLimitDecision Evaluate()
    {
        return _limiter.Evaluate(_context);
    }
}
