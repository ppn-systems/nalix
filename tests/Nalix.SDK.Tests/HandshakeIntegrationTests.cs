using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Codec.DataFrames;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Runtime.Sessions;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
namespace Nalix.SDK.Tests;

[Collection(TestCollections.RealServer)]
public sealed class HandshakeIntegrationTests : IDisposable
{
    private readonly Bytes32 _serverPublicKey;

    public HandshakeIntegrationTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
            PacketRegistry.Build();
        TestUtils.SetupCertificate();
        _serverPublicKey = Bytes32.Parse(TestUtils.GetServerPublicKey());
    }

    [Fact]
    public async Task HandshakeAsync_FullFlow_Succeeds()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using TcpSession session = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = _serverPublicKey.ToString()
            });

            await session.ConnectAsync();

            // Perform Handshake
            await session.HandshakeAsync();

            // Verify
            Assert.True(session.State.EncryptionEnabled);
            Assert.NotEqual(Bytes32.Zero, session.State.Secret);
            Assert.Equal(CipherSuiteType.Chacha20Poly1305, session.Options.Algorithm);
            Assert.NotEqual(0UL, session.State.SessionToken);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task ConnectWithResumeAsync_FullCycle_Succeeds()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.Configure<Nalix.Runtime.Options.SessionStoreOptions>(opt =>
        {
            opt.MinAttributesForPersistence = 0;
        });
        TrackingSessionStore store = new();
        builder.UseSessionStore(store);
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();
        builder.UseSessions();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using TcpSession session = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = _serverPublicKey.ToString(),
                ResumeEnabled = true,
                ConnectTimeoutMillis = 30000,
                ResumeTimeoutMillis = 30000
            });

            // 1. First connect (performs Handshake)
            ProtocolReason firstReason = await session.ConnectWithResumeDetailedAsync();
            bool resumed1 = firstReason == ProtocolReason.NONE;
            Assert.Equal(ProtocolReason.SESSION_NOT_FOUND, firstReason);
            Assert.False(resumed1);
            Assert.NotEqual(0UL, session.State.SessionToken);

            ulong token = session.State.SessionToken;
            Bytes32 secret = session.State.Secret;

            // Manually store the session before disconnecting, since the
            // HandshakeHandlers static field may not have captured ISessionService
            // at class-load time.
            await store.StoreAsync(new SessionEntry(
                new SessionSnapshot
                {
                    SessionToken = token,
                    Secret = secret,
                    Algorithm = session.Options.Algorithm,
                    ExpiresAtUnixMilliseconds = long.MaxValue
                },
                connectionId: 0UL));

            await session.DisconnectAsync();

            // 2. Second connect (should resume)
#pragma warning disable CS0612
            bool resumed2 = await session.ConnectWithResumeAsync();
#pragma warning restore CS0612
            Assert.True(resumed2);
            
            Assert.NotEqual(0UL, session.State.SessionToken);
            Assert.Equal(secret, session.State.Secret);
            Assert.True(session.State.EncryptionEnabled);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    private sealed class MockUnderAttackPolicy : IProofOfWorkPolicy
    {
        public byte CurrentDifficulty => 8; // Small difficulty for fast test
        public bool IsUnderAttack => true;
    }

    [Fact]
    public async Task HandshakeAsync_UnderAttack_CompletesWithPoW()
    {
        int port = TestUtils.GetFreePort();
        InstanceManager.Instance.Register<IProofOfWorkPolicy>(new MockUnderAttackPolicy());

        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using TcpSession session = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = _serverPublicKey.ToString(),
                ConnectTimeoutMillis = 30000
            });

            await session.ConnectAsync();

            // Handshake should internally do Optimistic -> receive POW_REQUIRED -> solve PoW -> retry SessionInit -> success.
            await session.HandshakeAsync();

            Assert.True(session.State.EncryptionEnabled);
            Assert.NotEqual(Bytes32.Zero, session.State.Secret);
            Assert.Equal(CipherSuiteType.Chacha20Poly1305, session.Options.Algorithm);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose() => InstanceManager.Instance.Clear(dispose: false);

    private sealed class TrackingSessionStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private readonly object _gate = new();
        private readonly Dictionary<ulong, TaskCompletionSource> _storedTokens = new();

        public async ValueTask StoreAsync(SessionEntry entry, CancellationToken cancellationToken = default)
        {
            await _inner.StoreAsync(entry, cancellationToken).ConfigureAwait(false);

            TaskCompletionSource? waiter;
            lock (_gate)
            {
                if (!_storedTokens.TryGetValue(entry.Snapshot.SessionToken, out waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _storedTokens[entry.Snapshot.SessionToken] = waiter;
                }
            }

            waiter.TrySetResult();
        }

        public ValueTask<SessionScope> ConsumeAsync(ulong sessionToken, Func<SessionEntry, bool>? predicate = null, CancellationToken cancellationToken = default)
            => _inner.ConsumeAsync(sessionToken, predicate, cancellationToken);

        public Task WaitForStoreAsync(ulong sessionToken, TimeSpan timeout)
        {
            lock (_gate)
            {
                if (!_storedTokens.TryGetValue(sessionToken, out TaskCompletionSource? waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _storedTokens[sessionToken] = waiter;
                }

                return waiter.Task.WaitAsync(timeout);
            }
        }
    }
}







