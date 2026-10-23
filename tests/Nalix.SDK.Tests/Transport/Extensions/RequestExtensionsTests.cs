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
public sealed class RequestExtensionsTests : IDisposable
{
    public RequestExtensionsTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task TryRequestAsync_WhenServerResponds_ReturnsOk()
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
            TransportOptions options = new() { Address = "127.0.0.1", Port = (ushort)port };
            using TcpSession session = new(options);
            await session.ConnectAsync();
#pragma warning disable CS0612
            await session.HandshakeAsync();
#pragma warning restore CS0612

            TimeSync ping = new();
            ping.Initialize(ControlType.PING, 4321, PacketFlags.NONE);

            RequestOutcome<TimeSync> outcome = await session.TryRequestAsync<TimeSync>(
                ping,
                options: RequestOptions.Default,
                predicate: p => p.Type == ControlType.PONG && p.Header.SequenceId == 4321);

            Assert.Equal(RequestOutcomeKind.Ok, outcome.Kind);
            Assert.NotNull(outcome.Value);
            Assert.Equal(ControlType.PONG, outcome.Value!.Type);
            Assert.Null(outcome.Error);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task TryRequestAsync_WhenNoServerResponds_ReturnsTimedOut()
    {
        int port = TestEnvironment.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            TransportOptions options = new() { Address = "127.0.0.1", Port = (ushort)port };
            using TcpSession session = new(options);
            await session.ConnectAsync();

            TimeSync ping = new();
            ping.Initialize(ControlType.PING, 1234, PacketFlags.NONE);

            RequestOutcome<TimeSync> outcome = await session.TryRequestAsync<TimeSync>(
                ping,
                options: RequestOptions.Default.WithTimeout(100).WithRetry(0),
                predicate: _ => true);

            Assert.Equal(RequestOutcomeKind.TimedOut, outcome.Kind);
            Assert.Null(outcome.Value);
            Assert.IsType<TimeoutException>(outcome.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RequestAsync_WithoutPredicate_CorrelatesBySequenceId()
    {
        int port = TestEnvironment.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSystemControl();
        builder.UseTimeSync();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using TcpSession session = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await session.ConnectAsync();

            TimeSync ping = new();
            ping.Initialize(ControlType.PING, 0, PacketFlags.NONE);

            // predicate is null: RequestAsync must stamp SequenceId and correlate the response by SequenceId.
            TimeSync pong = await session.RequestAsync<TimeSync>(
                ping,
                options: RequestOptions.Default.WithTimeout(5000),
                predicate: null);

            Assert.NotEqual((ushort)0, ping.Header.SequenceId);
            Assert.Equal(ping.Header.SequenceId, pong.Header.SequenceId);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
