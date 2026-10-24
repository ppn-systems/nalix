// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Injection;
using Nalix.Environment.Hashing;
using Nalix.Hosting;
using Nalix.Network.Protocols;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Runtime.Sessions;
using Nalix.Network.Connections;
using Nalix.Runtime.Dispatching;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Extensions;

[Collection(TestCollections.RealServer)]
public sealed class ResumeExtensionsTests : IDisposable
{
    public ResumeExtensionsTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        PacketRegistry.Build();
TestEnvironment.SetupCertificate();
    }

    [Fact]
    public async Task ResumeSessionAsync_Successful_ReturnsNone()
    {
        int port = TestEnvironment.GetFreePort();
        ulong token = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
        byte[] secretBytes = new byte[32];
        secretBytes[0] = 0xAA;
        Bytes32 secret = new(secretBytes);

        InMemorySessionStore store = new();
        SessionService sessionService = new(store: store);
        ConnectionHub hub = new();

        // 1. Setup Server with real SessionStore
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.UseConnectionHub(hub);
        builder.UseSessionService(sessionService);
        builder.UseSessionStore(store);
        builder.Configure<Nalix.Runtime.Options.SessionStoreOptions>(opt => opt.MinAttributesForPersistence = 0);
        builder.ListenTcp<RobustIntegrationTestProtocol>().WithFactory(dispatch => new RobustIntegrationTestProtocol(dispatch, hub)).OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSessions();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            // 2. Pre-populate SessionStore via the injected store
            // We can use 'store' directly since we have the reference
            hub.Count.Should().Be(0); // Sanity check

            // Create a fake connection object to represent the "previous" connection
            SessionSnapshot snapshot = new()
            {
                SessionToken = token,
                Secret = secret,
                Algorithm = CipherSuiteType.Chacha20Poly1305,
                ExpiresAtUnixMilliseconds = long.MaxValue
            };
            SessionEntry entry = new(snapshot, 0UL);

            await store.StoreAsync(entry);

            // 3. Setup Client
            using TcpSession session = new(new TransportOptions 
            { 
                ResumeTimeoutMillis = 30000
            });
            session.State.Secret = secret;
            session.State.SessionToken = token;
            session.State.EncryptionEnabled = false;

            await session.ConnectAsync("127.0.0.1", (ushort)port);

            // 4. Perform Resume
#pragma warning disable CS0612
            ProtocolReason result = await session.ResumeSessionAsync();
#pragma warning restore CS0612

            // 5. Verify Result
            Assert.Equal(ProtocolReason.NONE, result);
            Assert.NotEqual(token, session.State.SessionToken); // Token should be rotated
            Assert.True(session.State.EncryptionEnabled);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task ResumeSessionAsync_InvalidProof_ReturnsTokenRevoked()
    {
        int port = TestEnvironment.GetFreePort();
        ulong token = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
        
        byte[] serverSecretBytes = new byte[32];
        serverSecretBytes[0] = 0xAA;
        Bytes32 serverSecret = new(serverSecretBytes);

        byte[] clientSecretBytes = new byte[32];
        clientSecretBytes[0] = 0xBB; // Different secret -> invalid proof
        Bytes32 clientSecret = new(clientSecretBytes);

        InMemorySessionStore store = new();
        SessionService sessionService = new(store: store);
        ConnectionHub hub = new();

        // 1. Setup Server
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.UseConnectionHub(hub);
        builder.UseSessionService(sessionService);
        builder.UseSessionStore(store);
        builder.Configure<Nalix.Runtime.Options.SessionStoreOptions>(opt => opt.MinAttributesForPersistence = 0);
        builder.ListenTcp<RobustIntegrationTestProtocol>().WithFactory(dispatch => new RobustIntegrationTestProtocol(dispatch, hub)).OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSessions();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            // 2. Pre-populate SessionStore via the injected store
            SessionSnapshot snapshot = new()
            {
                SessionToken = token,
                Secret = serverSecret,
                ExpiresAtUnixMilliseconds = long.MaxValue
            };
            SessionEntry entry = new(snapshot, 0UL);
            await store.StoreAsync(entry);

            // 2. Setup Client with WRONG secret
            using TcpSession session = new(new TransportOptions 
            { 
                ResumeTimeoutMillis = 10000
            });
            session.State.Secret = clientSecret;
            session.State.SessionToken = token;
            session.State.EncryptionEnabled = false;

            await session.ConnectAsync("127.0.0.1", (ushort)port);

            // 3. Perform Resume
#pragma warning disable CS0612
            ProtocolReason result = await session.ResumeSessionAsync();
#pragma warning restore CS0612

            // 4. Verify Result
            // SessionHandlers returns uniform SESSION_EXPIRED if proof is invalid to prevent token enumeration
            Assert.Equal(ProtocolReason.SESSION_EXPIRED, result);

            using SessionScope retained = await store.ConsumeAsync(token);
            Assert.True(retained.IsValid, "invalid resume proof must not consume the stored session token");
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenPreviousSessionHadHighSequences_ResetsSequencesForFreshTransport()
    {
        int port = TestEnvironment.GetFreePort();
        ulong token = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
        byte[] secretBytes = new byte[32];
        secretBytes[0] = 0xDD;
        Bytes32 secret = new(secretBytes);

        InMemorySessionStore store = new();
        SessionService sessionService = new(store: store);
        ConnectionHub hub = new();

        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.UseConnectionHub(hub);
        builder.UseSessionService(sessionService);
        builder.UseSessionStore(store);
        builder.Configure<Nalix.Runtime.Options.SessionStoreOptions>(opt => opt.MinAttributesForPersistence = 0);
        builder.ListenTcp<RobustIntegrationTestProtocol>().WithFactory(dispatch => new RobustIntegrationTestProtocol(dispatch, hub)).OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSessions();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            var attrs = Nalix.Framework.Memory.Objects.ObjectMap<Nalix.Abstractions.AttributeKey, object>.Rent();
            attrs[ConnectionAttributes.SequenceState] = new ConnectionSequenceState
            {
                TcpSendSequence = 50,
                TcpReceiveSequence = 50
            };

            SessionSnapshot snapshot = new()
            {
                SessionToken = token,
                Secret = secret,
                Algorithm = CipherSuiteType.Chacha20Poly1305,
                ExpiresAtUnixMilliseconds = long.MaxValue,
                Attributes = attrs
            };
            SessionEntry entry = new(snapshot, 0UL);

            await store.StoreAsync(entry);

            using TcpSession session = new(new TransportOptions
            {
                ResumeTimeoutMillis = 30000
            });
            session.State.Secret = secret;
            session.State.SessionToken = token;
            session.State.EncryptionEnabled = false;

            await session.ConnectAsync("127.0.0.1", (ushort)port);

#pragma warning disable CS0612
            ProtocolReason result = await session.ResumeSessionAsync();
#pragma warning restore CS0612

            Assert.Equal(ProtocolReason.NONE, result);

            IConnection serverConn = Assert.Single(hub.ListConnections());
            Assert.Equal(0u, serverConn.TCP.ReceiveSequence.Current());
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task ResumeSessionAsync_ExpiredSession_ReturnsSessionExpired()
    {
        int port = TestEnvironment.GetFreePort();
        ulong token = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
        byte[] secretBytes = new byte[32];
        secretBytes[0] = 0xCC;
        Bytes32 secret = new(secretBytes);

        InMemorySessionStore store = new();
        SessionService sessionService = new(store: store);
        ConnectionHub hub = new();

        // 1. Setup Server
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.UseConnectionHub(hub);
        builder.UseSessionService(sessionService);
        builder.UseSessionStore(store);
        builder.Configure<Nalix.Runtime.Options.SessionStoreOptions>(opt => opt.MinAttributesForPersistence = 0);
        builder.ListenTcp<RobustIntegrationTestProtocol>().WithFactory(dispatch => new RobustIntegrationTestProtocol(dispatch, hub)).OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSessions();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            // Note: SessionHandlers doesn't handle SESSION_EXPIRED directly if token not found,
            // it's handled by ConsumeAsync returning null.
            // But we don't store it at all to simulate "expired and scavenged" or "never existed".

            // 2. Setup Client
            using TcpSession session = new(new TransportOptions 
            { 
                Address = "127.0.0.1",
                Port = (ushort)port,
            });
            session.State.Secret = secret;
            session.State.SessionToken = token;
            session.State.EncryptionEnabled = false;

            await session.ConnectAsync("127.0.0.1", (ushort)port);

            // 3. Perform Resume
#pragma warning disable CS0612
            ProtocolReason result = await session.ResumeSessionAsync();
#pragma warning restore CS0612

            // 4. Verify Result
            Assert.Equal(ProtocolReason.SESSION_EXPIRED, result);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }
    public class RobustIntegrationTestProtocol : IntegrationTestProtocol
    {
        private readonly IConnectionHub _hub;
        public RobustIntegrationTestProtocol(IPacketDispatch dispatch, IConnectionHub hub) : base(dispatch)
        {
            _hub = hub;
        }

        public override void OnAccept(IConnection connection, CancellationToken cancellationToken = default)
        {
            connection.Attributes[ConnectionAttributes.OwnerHub] = _hub;
            base.OnAccept(connection, cancellationToken);
        }
    }

    public void Dispose() => InstanceManager.Instance.Clear(dispose: false);
}
