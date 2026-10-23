// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.Hosting.Tests.TestSupport;

namespace Nalix.Hosting.Tests.Application;

[Collection(TestCollections.RealServer)]
public sealed class GracefulShutdownTests : IDisposable
{
    public GracefulShutdownTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!Nalix.Codec.DataFrames.PacketRegistry.IsBuilt)
        {
            Nalix.Codec.DataFrames.PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task Deactivate_WithActiveConnections_CompletesWithinBoundedTime()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        List<TcpSession> clients = [];
        try
        {
            for (int i = 0; i < 3; i++)
            {
                TcpSession session = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
                await session.ConnectAsync().WaitAsync(TestUtils.Timeout);
                clients.Add(session);
            }

            foreach (TcpSession c in clients)
            {
                Assert.True(c.IsConnected);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task deactivate = app.DeactivateAsync(cts.Token);
            Task completed = await Task.WhenAny(deactivate, Task.Delay(TimeSpan.FromSeconds(15)));

            Assert.Same(deactivate, completed);
            await deactivate; // Propagate any exception; must not hang or throw past this point.
        }
        finally
        {
            foreach (TcpSession c in clients)
            {
                c.Dispose();
            }
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
