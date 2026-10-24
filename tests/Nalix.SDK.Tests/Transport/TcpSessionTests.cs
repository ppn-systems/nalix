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

namespace Nalix.SDK.Tests.Transport;

[Collection(TestCollections.RealServer)]
public sealed class TcpSessionTests : IDisposable
{
    public TcpSessionTests()
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
        int port = TestEnvironment.GetFreePort();
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
        int port = TestEnvironment.GetFreePort();
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

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnected_DoesNotRaiseOnDisconnected()
    {
        int port = TestEnvironment.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            using TcpSession session = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });

            int connectedCount = 0;
            int disconnectedCount = 0;
            session.OnConnected += (_, _) => Interlocked.Increment(ref connectedCount);
            session.OnDisconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);

            Task<Socket> accept1 = listener.AcceptSocketAsync();
            await session.ConnectAsync();
            using Socket s1 = await accept1;

            Task<Socket> accept2 = listener.AcceptSocketAsync();
            await session.ConnectAsync();
            using Socket s2 = await accept2;

            Assert.Equal(2, connectedCount);
            Assert.Equal(0, disconnectedCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task KeepAlive_WhenServerIsBlackholed_FaultsAndRaisesOnDisconnected()
    {
        int port = TestEnvironment.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            using TcpSession session = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                KeepAliveIntervalMillis = 100,
                KeepAliveTimeoutMillis = 100,
                KeepAliveMissThreshold = 2
            });

            TaskCompletionSource<Exception> disconnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnDisconnected += (_, ex) => disconnectedTcs.TrySetResult(ex);

            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            await session.ConnectAsync();
            using Socket blackholeServer = await acceptTask;
            _ = blackholeServer; // Accepts TCP connection and reads/sends nothing (black-hole).

            Exception ex = await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(ex);
            Assert.False(session.IsConnected);
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

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
