// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;

namespace Nalix.Network.Tests;

/// <summary>
/// The TCP transport writes the 2-byte length prefix into headroom reserved by the lease and sends
/// in place; leases without headroom still go through the copying path. Both must produce the same wire bytes.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class TcpReservedHeaderSendTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendAsyncCore_Lease_ProducesLengthPrefixedFrame(bool withHeadroom)
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

        byte[] payload = new byte[300];
        new Random(42).NextBytes(payload);

        using BufferLease lease = withHeadroom
            ? BufferLease.Rent(payload.Length, zeroOnDispose: false, BufferLease.TransportHeadroom)
            : BufferLease.Rent(payload.Length);
        payload.CopyTo(lease.SpanFull);
        lease.CommitLength(payload.Length);

        IConnection.ITransport transport = connection.TCP;
        await transport.SendAsyncCore(lease, CancellationToken.None);

        byte[] frame = new byte[payload.Length + 2];
        int read = 0;
        while (read < frame.Length)
        {
            int n = await client.ReceiveAsync(frame.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            _ = n.Should().BeGreaterThan(0);
            read += n;
        }

        _ = BinaryPrimitives.ReadUInt16LittleEndian(frame).Should().Be((ushort)(payload.Length + 2));
        _ = frame.AsSpan(2).ToArray().Should().Equal(payload);

        // The payload seen through the lease is unchanged by the in-place header write.
        _ = lease.Span.ToArray().Should().Equal(payload);
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
        _ = InstanceManager.Instance.GetOrCreateInstance<ObjectPoolManager>();
    }
}
