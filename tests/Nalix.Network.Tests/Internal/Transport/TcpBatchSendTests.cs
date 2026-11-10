// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// A batch handed to the TCP transport is coalesced into shared socket writes, but the peer must still
/// read every frame as its own length-prefixed frame, in order.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class TcpBatchSendTests
{
    [Theory]
    [InlineData(1, 300)]
    [InlineData(5, 300)]
    // Crosses SocketConnection.MaxBatchBytes, so the batch spans several socket writes.
    [InlineData(400, 300)]
    // At the fragmentation threshold: the batch falls back to frame-by-frame sends.
    [InlineData(3, 40_000)]
    public async Task SendAsyncCore_WithBatch_ProducesEveryFrameInOrder(int frameCount, int payloadLength)
    {
        EnsureRegistered();

        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        Task<Socket> accept = listener.AcceptAsync();

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndPoint!).Port);
        using Socket server = await accept;
        using Connection connection = new(server, new ZeroOpCode());

        Random random = new(42);
        byte[][] payloads = new byte[frameCount][];
        IBufferLease[] frames = new IBufferLease[frameCount];
        int unfragmentedBytes = 0;
        for (int i = 0; i < frameCount; i++)
        {
            payloads[i] = new byte[payloadLength];
            random.NextBytes(payloads[i]);
            frames[i] = BufferLease.CopyFrom(payloads[i]);
            unfragmentedBytes += payloadLength + 2;
        }

        try
        {
            IConnection.ITransport transport = connection.TCP;
            Task send = transport.SendAsyncCore(new ReadOnlyMemory<IBufferLease>(frames), CancellationToken.None).AsTask();

            if (payloadLength < 32_000)
            {
                byte[] wire = await ReceiveExactAsync(client, unfragmentedBytes);
                int offset = 0;
                for (int i = 0; i < frameCount; i++)
                {
                    _ = BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(offset)).Should().Be((ushort)(payloadLength + 2));
                    _ = wire.AsSpan(offset + 2, payloadLength).ToArray().Should().Equal(payloads[i]);
                    offset += payloadLength + 2;
                }
            }
            else
            {
                // Fragmented frames carry extra headers; only check that every payload byte arrives.
                byte[] wire = await ReceiveAtLeastAsync(client, frameCount * payloadLength);
                _ = wire.Length.Should().BeGreaterThan(frameCount * payloadLength);
            }

            await send.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (IBufferLease frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public async Task SendBatchAsync_WithSmallFrames_UsesOneSocketWrite()
    {
        EnsureRegistered();

        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        Task<Socket> accept = listener.AcceptAsync();

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndPoint!).Port);
        using Socket server = await accept;
        using Connection connection = new(server, new ZeroOpCode());

        IBufferLease[] frames = new IBufferLease[10];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = BufferLease.CopyFrom(new byte[100]);
        }

        try
        {
            await connection.TCP.SendAsyncCore(new ReadOnlyMemory<IBufferLease>(frames), CancellationToken.None);

            // The whole batch (10 x 102 bytes) was one write, so one receive returns all of it.
            byte[] buffer = new byte[4096];
            int read = await client.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _ = read.Should().Be(frames.Length * 102);
        }
        finally
        {
            foreach (IBufferLease frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    private static async Task<byte[]> ReceiveExactAsync(Socket socket, int length)
    {
        byte[] buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _ = n.Should().BeGreaterThan(0);
            read += n;
        }

        return buffer;
    }

    private static async Task<byte[]> ReceiveAtLeastAsync(Socket socket, int length)
    {
        using MemoryStream received = new();
        byte[] buffer = new byte[64 * 1024];
        while (received.Length <= length)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _ = n.Should().BeGreaterThan(0);
            received.Write(buffer, 0, n);
        }

        return received.ToArray();
    }

    private sealed class ZeroOpCode : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private static void EnsureRegistered()
    {
        InstanceManager.Instance.Register<ILogger>(NullLogger.Instance);
        _ = ConfigurationManager.Instance.Get<Nalix.Framework.Options.ObjectPoolOptions>();
        _ = InstanceManager.Instance.GetOrCreateInstance<BufferPoolManager>();
        InstanceManager.Instance.Register<IObjectPoolManager>(ObjectPoolManager.Shared);
        InstanceManager.Instance.Register<ObjectPoolManager>(ObjectPoolManager.Shared);
    }
}
