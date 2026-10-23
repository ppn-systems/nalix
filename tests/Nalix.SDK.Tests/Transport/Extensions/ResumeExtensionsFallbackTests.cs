// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.SDK.Tests.TestSupport;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Extensions;

[Collection(TestCollections.RealServer)]
public sealed class ResumeExtensionsFallbackTests : IDisposable
{
    public ResumeExtensionsFallbackTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task ConnectWithResumeDetailedAsync_WhenResumeIsRejected_FallsBackToHandshakeWithoutExtraEvents()
    {
        int port = TestEnvironment.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
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
                ServerPublicKey = TestEnvironment.GetServerPublicKey(),
                ResumeEnabled = true,
                ResumeFallbackToHandshake = true
            });

            // Populate an unknown/expired session token so ResumeSessionAsync is attempted and rejected.
            byte[] secretBytes = new byte[32];
            secretBytes[0] = 0x5A;
            session.State.Secret = new Nalix.Abstractions.Primitives.Bytes32(secretBytes);
            session.State.SessionToken = 999999UL;
            session.State.EncryptionEnabled = true;

            int connectedCount = 0;
            int disconnectedCount = 0;
            int readyCount = 0;
            session.OnConnected += (_, _) => Interlocked.Increment(ref connectedCount);
            session.OnDisconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);
            session.OnReady += (_, _) => Interlocked.Increment(ref readyCount);

            ProtocolReason reason = await session.ConnectWithResumeDetailedAsync();

            Assert.NotEqual(ProtocolReason.NONE, reason);
            Assert.True(session.IsConnected);
            Assert.True(session.State.EncryptionEnabled);
            Assert.Equal(1, connectedCount);
            Assert.Equal(0, disconnectedCount);
            Assert.Equal(1, readyCount);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
