// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.DataFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport;

namespace Nalix.SDK.Tests.Transport.Internal;

[Collection(TestCollections.RealServer)]
public sealed class ReconnectSupervisorTests : IDisposable
{
    public ReconnectSupervisorTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        TestEnvironment.SetupCertificate();
    }

    [Fact]
    public async Task ReconnectLoop_WhenConnectionDropsDuringReauthCallback_ReconnectsAgain()
    {
        int port = TestEnvironment.GetFreePort();

        using NetworkApplication app0 = BuildSecureServer(port);
        await app0.ActivateAsync();

        using TcpSession session = new(new TransportOptions
        {
            Address = "127.0.0.1",
            Port = (ushort)port,
            ServerPublicKey = TestEnvironment.GetServerPublicKey(),
            AutoReconnectEnabled = true,
            ReconnectBaseDelayMillis = 50,
            ReconnectMaxDelayMillis = 200,
        });

        int drops = 0;
        int reauthCalls = 0;
        NetworkApplication? app2 = null;
        TaskCompletionSource dropDuringCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int dropsAtCallbackStart = -1;
        NetworkApplication? app1 = null;

        session.OnDisconnected += (_, _) =>
        {
            int seen = Interlocked.Increment(ref drops);
            int baseline = Volatile.Read(ref dropsAtCallbackStart);
            if (baseline >= 0 && seen > baseline)
            {
                _ = dropDuringCallback.TrySetResult();
            }
        };

        session.OnReauthenticateAsync = async _ =>
        {
            if (Interlocked.Increment(ref reauthCalls) != 1)
            {
                return;
            }

            Volatile.Write(ref dropsAtCallbackStart, Volatile.Read(ref drops));

            // The connection dies while the supervisor is still inside the callback; the callback swallows it.
            await app1!.DeactivateAsync();
            await dropDuringCallback.Task.WaitAsync(TimeSpan.FromSeconds(10));
            app2 = BuildSecureServer(port);
            await app2.ActivateAsync();
        };

        try
        {
            await session.ConnectAsync();
            Assert.True(session.IsConnected);

            // First drop starts the reconnect loop; app1 takes over the port for the first reconnect.
            await app0.DeactivateAsync();
            app1 = BuildSecureServer(port);
            await app1.ActivateAsync();

            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !(Volatile.Read(ref reauthCalls) >= 2 && session.IsConnected))
            {
                await Task.Delay(50);
            }

            Assert.True(
                Volatile.Read(ref reauthCalls) >= 2 && session.IsConnected,
                "Supervisor published ready on a dead session and never reconnected.");
        }
        finally
        {
            await session.DisconnectAsync();
            if (app1 is not null)
            {
                await app1.DeactivateAsync();
                app1.Dispose();
            }

            if (app2 is not null)
            {
                await app2.DeactivateAsync();
                app2.Dispose();
            }
        }
    }

    private static NetworkApplication BuildSecureServer(int port)
    {
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();
        builder.UseTimeSync();
        return builder.Build();
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
