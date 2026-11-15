// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Identity;
using Nalix.Codec.DataFrames;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Sequencing;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Network.Options;
using Nalix.Environment.Configuration;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Tests.Shared;
using Xunit;

namespace Nalix.Integration.Tests.EndToEnd;

/// <summary>
/// Resume scenarios over loopback: a real server with sessions enabled, driven by the public SDK client.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class SessionResumeEndToEndTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly RequestOptions s_request = RequestOptions.Default.WithTimeout(5_000).WithEncrypt();

    private readonly NetworkConfigSnapshot _configSnapshot = new();
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-resume-{Guid.NewGuid():N}.private");

    public SessionResumeEndToEndTests()
    {
        File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = Host;

        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    public void Dispose()
    {
        _configSnapshot.Dispose();
        File.Delete(_certificatePath);
        InstanceManager.Instance.Clear(dispose: false);
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenTokenUnknown_ReturnsSessionExpiredPromptly()
    {
        ushort port = (ushort)TestPorts.Next();
        await using NetworkApplication app = await this.StartServerAsync(port);

        using TcpSession client = new(CreateOptions(port));
        await HandshakeAndExchangeAsync(client);

        // Forget the server-side session: the token is now unknown to the server.
        await client.DisconnectAsync().WaitAsync(s_timeout);
        await client.ConnectAsync().WaitAsync(s_timeout);
        client.State.SessionToken = Snowflake.NewId(SnowflakeType.Session).ToUInt64();

        Stopwatch clock = Stopwatch.StartNew();
#pragma warning disable CS0612
        ProtocolReason reason = await client.ResumeSessionAsync().AsTask().WaitAsync(s_timeout);
#pragma warning restore CS0612
        clock.Stop();

        Assert.Equal(ProtocolReason.SESSION_EXPIRED, reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Rejection took {clock.Elapsed}; expected the server's reply, not the resume timeout.");
    }

    [Fact]
    public async Task ConnectWithResumeDetailedAsync_WhenTokenExpired_FallsBackToHandshake()
    {
        ushort port = (ushort)TestPorts.Next();
        await using NetworkApplication app = await this.StartServerAsync(port);

        using TcpSession client = new(CreateOptions(port));
        await HandshakeAndExchangeAsync(client);

        await client.DisconnectAsync().WaitAsync(s_timeout);
        client.State.SessionToken = Snowflake.NewId(SnowflakeType.Session).ToUInt64();

        Stopwatch clock = Stopwatch.StartNew();
        ProtocolReason reason = await client.ConnectWithResumeDetailedAsync().AsTask().WaitAsync(s_timeout);
        clock.Stop();

        Assert.Equal(ProtocolReason.SESSION_EXPIRED, reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Fallback took {clock.Elapsed}; expected the server's reply, not the resume timeout.");
        Assert.True(client.State.EncryptionEnabled);

        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 5, Text = "fallback" }, s_request).AsTask().WaitAsync(s_timeout);
        Assert.Equal("FALLBACK", response.Text);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    private static async Task HandshakeAndExchangeAsync(TcpSession client)
    {
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        // Encrypted server replies advance the client's receive counter, as in a long-lived session.
        // Plain echo replies are not sequenced, so the batch handler (which replies encrypted) is used.
        int received = 0;
        TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = client.On<EchoPacket>(packet =>
        {
            if (Interlocked.Increment(ref received) == 5)
            {
                _ = all.TrySetResult();
            }
        });
        await client.SendAsync(new EchoBatchRequest { Count = 5 }, encrypt: true).WaitAsync(s_timeout);
        await all.Task.WaitAsync(s_timeout);

        Assert.True(ReceiveSequence(client) >= 5, "The client's receive counter must be warm before the resume attempt.");
    }

    private static uint ReceiveSequence(TcpSession client)
    {
        object reader = typeof(TcpSession).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        return ((SequenceCounter)reader.GetType().GetProperty("Sequence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!).Current();
    }

    private static TransportOptions CreateOptions(ushort port) => new()
    {
        Address = Host,
        Port = port,
        CompressionEnabled = false,
        ConnectTimeoutMillis = 5_000,
        ResumeTimeoutMillis = 3_000,
    };

    private async Task<NetworkApplication> StartServerAsync(ushort port)
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        _ = builder.UseSecureConnections(_certificatePath).UseTimeSync();
        _ = builder.UseSessions();
        _ = builder.MapHandlers(typeof(EchoHandlers));
        _ = builder.MapTcp<DefaultProtocol>().OnPort(port).Bind();

        NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(s_timeout);
        return app;
    }
}
