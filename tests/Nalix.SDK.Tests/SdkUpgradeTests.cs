// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

#if DEBUG
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
using Xunit;

namespace Nalix.SDK.Tests;

[Collection("RealServerTests")]
public sealed class SdkUpgradeTests : IDisposable
{
    public SdkUpgradeTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task SendAsync_WhenSequenceIdUnset_AutoStampsIncrementingValues()
    {
        int port = TestUtils.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
        try
        {
            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            await client.ConnectAsync();
            using Socket serverSide = await acceptTask;

            TimeSync first = new();
            Assert.Equal((ushort)0, first.Header.SequenceId);
            await client.SendAsync(first, CancellationToken.None);
            ushort firstSeq = await ReadSequenceIdAsync(serverSide);

            TimeSync second = new();
            await client.SendAsync(second, CancellationToken.None);
            ushort secondSeq = await ReadSequenceIdAsync(serverSide);

            Assert.NotEqual((ushort)0, firstSeq);
            Assert.Equal((ushort)(firstSeq + 1), secondSeq);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SendAsync_WhenSequenceIdAlreadySet_DoesNotOverwrite()
    {
        int port = TestUtils.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
        try
        {
            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            await client.ConnectAsync();
            using Socket serverSide = await acceptTask;

            TimeSync request = new();
            request.Initialize(ControlType.PING, 7777, PacketFlags.NONE);

            await client.SendAsync(request, CancellationToken.None);
            ushort observedSeq = await ReadSequenceIdAsync(serverSide);

            Assert.Equal((ushort)7777, observedSeq);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<ushort> ReadSequenceIdAsync(Socket socket)
    {
        byte[] lengthBuf = new byte[2];
        await ReadExactAsync(socket, lengthBuf);
        ushort totalLen = BinaryPrimitives.ReadUInt16LittleEndian(lengthBuf);

        byte[] body = new byte[totalLen - 2];
        await ReadExactAsync(socket, body);

        // Common packet header layout: OpCode(2) Flags(1) Priority(1) SequenceId(2) ...
        return BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4, 2));
    }

    private static async Task ReadExactAsync(Socket socket, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None);
            Assert.True(n > 0, "Socket closed before the expected bytes arrived.");
            read += n;
        }
    }

    [Fact]
    public async Task TryRequestAsync_ControlPacket_ReturnsOk()
    {
        int port = TestUtils.GetFreePort();
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
        int port = TestUtils.GetFreePort();
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
    public async Task StreamAsync_WhenInactivityTimeoutElapses_ThrowsTimeoutException()
    {
        int port = TestUtils.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
        try
        {
            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            await client.ConnectAsync();
            using Socket serverSide = await acceptTask;
            _ = serverSide; // Server never sends anything -> stream must stall.

            TimeSync request = new();
            request.Initialize(ControlType.PING, 1, PacketFlags.NONE);

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                await foreach (NullablePacketStreamTests.NullableStreamItem _ in client.StreamAsync<NullablePacketStreamTests.NullableStreamItem>(
                    request, ct: cts.Token, inactivityTimeoutMs: 100))
                {
                }
            });
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
#endif
