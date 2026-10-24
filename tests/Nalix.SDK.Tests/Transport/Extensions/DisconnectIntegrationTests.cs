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
public sealed class DisconnectIntegrationTests : IDisposable
{
    public DisconnectIntegrationTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        PacketRegistry.Build();
        TestEnvironment.SetupCertificate();
    }

    [Fact]
    public async Task DisconnectGracefullyAsync_WhenConnected_Succeeds()
    {
        int port = TestEnvironment.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        
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

            Assert.True(session.IsConnected);

            // 3. Graceful Disconnect
            await session.DisconnectGracefullyAsync(ProtocolReason.SERVER_SHUTDOWN);

            // 4. Verify
            Assert.False(session.IsConnected);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }
    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
