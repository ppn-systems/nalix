// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
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
public sealed class TimeSyncExtensionsTests : IDisposable
{
    public TimeSyncExtensionsTests()
    {
    
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        PacketRegistry.Build();
        TestEnvironment.SetupCertificate();
    }

    [Fact]
    public async Task SyncTimeAsync_WhenSuccessful_ReturnsRttAndAdjusted()
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
                Port = (ushort)port,
                TimeSyncEnabled = true
            };

            using var session = new TcpSession(options);
            await session.ConnectAsync();
#pragma warning disable CS0612
            await session.HandshakeAsync();
#pragma warning restore CS0612

            (double rtt, double adjusted) = await session.SyncTimeAsync(timeoutMs: 10000);
            
            Assert.True(rtt >= 0);
            // Adjusted might be 0 if clocks are perfectly synced, but usually it's non-zero
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }
    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
