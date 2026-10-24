// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Extensions;

[Collection(TestCollections.RealServer)]
public sealed class PingExtensionsTests : IDisposable
{
    public PingExtensionsTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
            PacketRegistry.Build();
        TestEnvironment.SetupCertificate();
    }

    [Fact]
    public async Task PingAsync_WithRealServer_ReturnsPositiveRtt()
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
            using var session = new TcpSession(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port
            });

            await session.ConnectAsync();
#pragma warning disable CS0612
            await session.HandshakeAsync();
#pragma warning restore CS0612

            double rtt = await session.PingAsync(timeoutMs: 2000);
            
            Assert.True(rtt >= 0, $"RTT should be positive, got {rtt}");
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }
    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
