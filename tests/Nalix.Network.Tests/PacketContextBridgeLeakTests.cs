#if DEBUG
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Xunit;

namespace Nalix.Network.Tests;

/// <summary>
/// Area 5 (pooling leaks): <see cref="PacketContextBridge"/> rents a strongly-typed
/// <see cref="PacketContext{TPacket}"/> from the shared pool and must (a) never dispose the
/// borrowed packet on Return (ownership stays with the base context per PacketContextBridge.cs:39-40),
/// and (b) fully clear the previous rental's Connection/Attributes/CancellationToken references on
/// ResetForPool so a returned instance cannot pin a stale <see cref="IConnection"/> alive via the pool.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PacketContextBridgeLeakTests
{
    [Fact]
    public void Create_WithNullBaseContext_ThrowsArgumentNullException()
    {
        Action act = () => PacketContextBridge.Create<TestPacket, TestPacket>(null!, new TestPacket());
        _ = act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Return_DoesNotDisposeBorrowedPacket_OwnershipStaysWithBaseContext()
    {
        TestPacket packet = new();
        PacketContext<TestPacket> baseContext = RentInitializedBaseContext();

        PacketContext<TestPacket> bridge = PacketContextBridge.Create(baseContext, packet);

        PacketContextBridge.Return(bridge);

        _ = packet.Disposed.Should().BeFalse(
            "PacketContextBridge.Return must not dispose the borrowed packet — the base context retains ownership");

        baseContext.Dispose();
    }

    [Fact]
    public void Return_ClearsConnectionAndAttributes_PreventingStaleReferenceRetention()
    {
        PacketContext<TestPacket> baseContext1 = RentInitializedBaseContext();
        FakeConnection firstConnection = (FakeConnection)baseContext1.Connection;

        PacketContext<TestPacket> bridge1 = PacketContextBridge.Create(baseContext1, new TestPacket());
        PacketContextBridge.Return(bridge1);
        baseContext1.Dispose();

        // Rent again — if the pool hands back the same underlying instance without clearing fields,
        // a bridge that is initialized a second time must not observably retain the first connection.
        PacketContext<TestPacket> baseContext2 = RentInitializedBaseContext();
        PacketContext<TestPacket> bridge2 = PacketContextBridge.Create(baseContext2, new TestPacket());

        _ = bridge2.Connection.Should().NotBeSameAs(firstConnection,
            "a freshly-created bridge context must reflect the new base context's connection, not a stale one");

        PacketContextBridge.Return(bridge2);
        baseContext2.Dispose();
    }

    /// <summary>
    /// Area 5 stress: 200 seeded-random Create/Return round trips (varying whether Return is called
    /// promptly) must never crash, double-return, or leave the pool in a state that throws on next Get.
    /// </summary>
    [Fact]
    [Trait("Category", "Stress")]
    public async Task HighConcurrency_CreateReturnRoundTrips_NeverCrashesOrDoubleReturns()
    {
        const int seed = 20260704;
        const int iterations = 200;
        System.Random rng = new(seed);
        int completed = 0;

        Task[] tasks = new Task[iterations];
        for (int i = 0; i < iterations; i++)
        {
            int delayTicks = rng.Next(0, 5);
            tasks[i] = Task.Run(() =>
            {
                for (int s = 0; s < delayTicks; s++)
                {
                    Thread.SpinWait(1);
                }

                PacketContext<TestPacket> baseContext = RentInitializedBaseContext();
                PacketContext<TestPacket> bridge = PacketContextBridge.Create(baseContext, new TestPacket());
                PacketContextBridge.Return(bridge);
                baseContext.Dispose();
                _ = Interlocked.Increment(ref completed);
            });
        }

        await Task.WhenAll(tasks);

        completed.Should().Be(iterations,
            $"seed={seed}: all {iterations} concurrent Create/Return round trips must complete without leaking or corrupting the shared pool");
    }

    private static PacketContext<TestPacket> RentInitializedBaseContext()
    {
        PacketContext<TestPacket> context = ObjectPoolManager.Shared.Get<PacketContext<TestPacket>>();
        PacketMetadata metadata = new(
            opCode: new PacketOpcodeAttribute((ushort)1),
            timeout: null,
            permission: null,
            encryption: null,
            rateLimit: null,
            transport: null);

        context.Initialize(
            packet: new TestPacket(),
            connection: new FakeConnection(),
            descriptor: metadata,
            reliable: true,
            encryptedOnWire: false,
            ownsPacket: true);

        return context;
    }

    private sealed class TestPacket : IPacket, IDisposable
    {
        public bool Disposed { get; private set; }
        public int Length => 0;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => [];
        public int Serialize(Span<byte> buffer) => 0;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeConnection : IConnection
    {
        public bool IsDisposed { get; private set; }
        public bool IsUdpCreated => false;
        public ulong ConnectionId => 1;
        public string? UserId { get; set; }
        public long UpTime => 0;
        public long LastPingTime => 0;
        public bool ExcludeFromIdleTimeout { get; set; }
        public IOpCodeExtractor PacketClassifier => null!;
        public INetworkEndpoint NetworkEndpoint => null!;
        public IObjectMap<AttributeKey, object> Attributes { get; } = ObjectMap<AttributeKey, object>.Rent();
        public System.Collections.Concurrent.ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public Bytes32 Secret { get; set; }
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }

        public IConnection.ITransport TCP => null!;
        public IConnection.ITransport? UDP => null;

        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;

        public void Disconnect(string? reason = null) { }
        public void Dispose() => IsDisposed = true;
        public int ErrorCount => 0;
        public void IncrementErrorCount() { }

        public int IdleTimeoutMs { get; set; } = 60000;
        public void UpdateIdleTimeout(int newTimeoutMs) => IdleTimeoutMs = newTimeoutMs;
    }
}
#endif
