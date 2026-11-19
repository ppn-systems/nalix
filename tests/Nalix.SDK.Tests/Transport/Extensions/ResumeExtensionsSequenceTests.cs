// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Runtime.Sessions;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.SDK.Tests.TestSupport;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Extensions;

[Collection(TestCollections.RealServer)]
public sealed class ResumeExtensionsSequenceTests : IDisposable
{
    private readonly Bytes32 _serverPublicKey;

    public ResumeExtensionsSequenceTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        TestEnvironment.SetupCertificate();
        _serverPublicKey = Bytes32.Parse(TestEnvironment.GetServerPublicKey());
    }

    [Fact]
    public async Task ConnectWithResumeDetailedAsync_WhenEncryptedTrafficWasExchanged_ResumesAndKeepsFramesAccepted()
    {
        int port = TestEnvironment.GetFreePort();
        CountingSessionStore store = new();

        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.Configure<Nalix.Runtime.Options.SessionStoreOptions>(opt => opt.MinAttributesForPersistence = 0);
        builder.UseSessionStore(store);
        builder.MapTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();
        builder.UseTimeSync();
        builder.UseSessions();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            TransportOptions options = new()
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = _serverPublicKey.ToString(),
                ResumeEnabled = true,
                ConnectTimeoutMillis = 30000,
                ResumeTimeoutMillis = 30000
            };

            using TcpSession session = new(options);

            ProtocolReason first = await session.ConnectWithResumeDetailedAsync();
            Assert.Equal(ProtocolReason.SESSION_NOT_FOUND, first);
            ulong token = session.State.SessionToken;

            // Advance both directions of the sequence before the disconnect.
            for (ushort i = 1; i <= 3; i++)
            {
                await AssertPingAsync(session, i);
            }

            // The handshake stores the session once; the server stores it again on disconnect with the live sequences.
            await session.DisconnectAsync();
            await store.WaitForStoresAsync(token, expected: 2, TimeSpan.FromSeconds(10));

            ProtocolReason resumed = await session.ConnectWithResumeDetailedAsync();
            Assert.Equal(ProtocolReason.NONE, resumed);

            // Frames after the resume must still be accepted by the server and answered by the client.
            for (ushort i = 4; i <= 6; i++)
            {
                await AssertPingAsync(session, i);
            }
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    // Subscribes to every SDK diagnostic while these tests run, so the diagnostics on the reconnect, resume,
    // handshake and request paths they take are built and written (and cannot throw) instead of skipped.
    private readonly DiagnosticCapture _diagnostics = new(string.Empty);

    public void Dispose()
    {
        _diagnostics.Dispose();
        InstanceManager.Instance.Clear(dispose: false);
    }

    private static async Task AssertPingAsync(TcpSession session, ushort sequenceId)
    {
        TimeSync ping = new();
        ping.Initialize(ControlType.PING, sequenceId, PacketFlags.NONE);

        TimeSync response = await session.RequestAsync<TimeSync>(
            ping,
            options: RequestOptions.Default.WithEncrypt(),
            predicate: p => p.Type == ControlType.PONG && p.Header.SequenceId == sequenceId);

        Assert.Equal(ControlType.PONG, response.Type);
    }

    /// <summary>
    /// Wraps the in-memory store and counts how many times each session token was stored.
    /// </summary>
    private sealed class CountingSessionStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();
        private readonly ConcurrentDictionary<ulong, int> _stores = new();

        public async ValueTask StoreAsync(SessionEntry entry, CancellationToken cancellationToken = default)
        {
            await _inner.StoreAsync(entry, cancellationToken).ConfigureAwait(false);
            _ = _stores.AddOrUpdate(entry.Snapshot.SessionToken, 1, static (_, count) => count + 1);
        }

        public ValueTask<SessionScope> ConsumeAsync(ulong sessionToken, Func<SessionEntry, bool>? predicate = null, CancellationToken cancellationToken = default)
            => _inner.ConsumeAsync(sessionToken, predicate, cancellationToken);

        public async Task WaitForStoresAsync(ulong sessionToken, int expected, TimeSpan timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!_stores.TryGetValue(sessionToken, out int count) || count < expected)
            {
                if (clock.Elapsed > timeout)
                {
                    throw new TimeoutException($"Session {sessionToken} was not stored {expected} times.");
                }

                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }
}
