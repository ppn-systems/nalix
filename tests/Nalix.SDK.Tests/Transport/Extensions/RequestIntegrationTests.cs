// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Extensions;

[Collection(TestCollections.RealServer)]
public sealed class RequestIntegrationTests : IDisposable
{
    public RequestIntegrationTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        PacketRegistry.Build();
        TestEnvironment.SetupCertificate();
    }

    [Fact]
    public async Task RequestAsync_ControlPacket_Succeeds()
    {
        int port = TestEnvironment.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();
        builder.UseTimeSync();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port
            };

            using var session = new TcpSession(options);
            await session.ConnectAsync();
#pragma warning disable CS0612
            await session.HandshakeAsync();
#pragma warning restore CS0612

            // PING expects PONG (which is a TimeSync packet with same Seq)
            var ping = new TimeSync();
            ping.Initialize(ControlType.PING, 1234, PacketFlags.NONE);

            TimeSync response = await session.RequestAsync<TimeSync>(
                ping,
                options: RequestOptions.Default,
                predicate: p => p.Type == ControlType.PONG && p.Header.SequenceId == 1234);

            Assert.Equal(ControlType.PONG, response.Type);
            Assert.Equal(1234u, response.Header.SequenceId);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task RequestAsync_WhenTimeout_ThrowsTimeoutException()
    {
        // Connect to a port that doesn't have a Nalix server
        int port = TestEnvironment.GetFreePort();
        
        // We don't start the app, so it won't respond
        var options = new TransportOptions
        {
            Address = "127.0.0.1",
            Port = (ushort)port
        };

        // Wait! If we don't start the server, ConnectAsync might fail.
        // We need a server that accepts but doesn't respond.
        System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            using var session = new TcpSession(options);
            await session.ConnectAsync();

            var ping = new TimeSync();
            ping.Initialize(ControlType.PING, 1234, PacketFlags.NONE);

            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await session.RequestAsync<TimeSync>(
                    ping,
                    options: RequestOptions.Default.WithTimeout(100).WithRetry(0),
                    predicate: _ => true));
        }
        finally
        {
            listener.Stop();
        }
    }
    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
