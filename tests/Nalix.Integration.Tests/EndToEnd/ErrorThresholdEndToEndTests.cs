// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Configuration;
using Nalix.Network.Options;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Integration.Tests.TestSupport;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Tests.Shared;
using Xunit;

namespace Nalix.Integration.Tests.EndToEnd;

/// <summary>
/// Per-connection error budget over real sockets: denied requests count toward <c>MaxErrorThreshold</c>, and a
/// correct client never does.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class ErrorThresholdEndToEndTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);

    // The server's Connection captures its options once per process, so the test uses the production default
    // rather than mutating the shared configuration instance.
    private static readonly int s_threshold = new ConnectionGuardOptions().MaxErrorThreshold;

    private readonly string _certificatePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nalix-threshold-{Guid.NewGuid():N}.private");

    public ErrorThresholdEndToEndTests()
    {
        System.IO.File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");

        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    public void Dispose()
    {
        System.IO.File.Delete(_certificatePath);
        InstanceManager.Instance.Clear(dispose: false);
    }

    [Fact]
    public async Task SendAsync_WhenDeniedRequestsExceedThreshold_DisconnectsClient()
    {
        ushort port = (ushort)TestPorts.Next();
        await using NetworkApplication app = await StartServerAsync(port, secure: false);

        using TcpSession client = new(CreateOptions(port));
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnDisconnected += (_, _) => disconnected.TrySetResult();
        await client.ConnectAsync().WaitAsync(s_timeout);

        for (int i = 0; i < s_threshold + 1; i++)
        {
            await client.SendAsync(new EchoAdminPacket { Sequence = i }, encrypt: false).WaitAsync(s_timeout);
        }

        await disconnected.Task.WaitAsync(s_timeout);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task HandshakeAndAuthorizedRequests_PastThreshold_KeepConnectionOpen()
    {
        ushort port = (ushort)TestPorts.Next();
        await using NetworkApplication app = await StartServerAsync(port, secure: true);

        using TcpSession client = new(CreateOptions(port));
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnDisconnected += (_, _) => disconnected.TrySetResult();
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        for (int i = 0; i <= s_threshold; i++)
        {
            EchoPacket response = await client.RequestAsync<EchoPacket>(
                new EchoPacket { Sequence = i, Text = "ok" },
                RequestOptions.Default.WithTimeout(5_000).WithEncrypt()).AsTask().WaitAsync(s_timeout);

            Assert.Equal(i + 1, response.Sequence);
        }

        Assert.False(disconnected.Task.IsCompleted);
        Assert.True(client.IsConnected);
        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    private static TransportOptions CreateOptions(ushort port) => new()
    {
        Address = Host,
        Port = port,
        CompressionEnabled = false,
        ConnectTimeoutMillis = 5_000,
    };

    private async Task<NetworkApplication> StartServerAsync(ushort port, bool secure)
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        if (secure)
        {
            _ = builder.UseSecureConnections(_certificatePath).UseTimeSync();
        }

        _ = builder.MapHandlers(typeof(EchoHandlers));
        _ = builder.MapHandlers(typeof(EchoAdminHandlers));
        _ = builder.MapTcp<DefaultProtocol>().OnPort(port).Bind();

        NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(s_timeout);
        return app;
    }
}
