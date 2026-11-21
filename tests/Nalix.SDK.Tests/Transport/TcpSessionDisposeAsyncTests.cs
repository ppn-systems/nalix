// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Reflection;
using System.Threading.Tasks;
using Nalix.Codec.DataFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport;
using Xunit;

namespace Nalix.SDK.Tests.Transport;

/// <summary>
/// <see cref="TransportSession.DisposeAsync"/> completes only after the receive loop has stopped, so no background work
/// of the session runs once the call returns. <see cref="TransportSession.Dispose()"/> only starts the shutdown.
/// </summary>
[Collection(TestCollections.RealServer)]
public sealed class TcpSessionDisposeAsyncTests : IDisposable
{
    public TcpSessionDisposeAsyncTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        TestEnvironment.SetupCertificate();
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);

    [Fact]
    public async Task DisposeAsync_WhenConnected_ReturnsAfterTheReceiveLoopHasStopped()
    {
        int port = TestEnvironment.GetFreePort();
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.MapTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            TcpSession session = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await session.ConnectAsync();

            Task receiveLoop = (Task)typeof(TcpSession)
                .GetField("_loopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(session)!;
            Assert.False(receiveLoop.IsCompleted);

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(receiveLoop.IsCompleted, "DisposeAsync must not return while the receive loop is still running");
            Assert.False(session.IsConnected);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }
}
