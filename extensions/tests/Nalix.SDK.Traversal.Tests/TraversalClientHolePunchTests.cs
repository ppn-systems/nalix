// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Nalix.SDK.Traversal.Tests.TestSupport;
using Nalix.Traversal.Packets;

namespace Nalix.SDK.Traversal.Tests;

/// <summary>
/// Verifies the UDP hole-punching step of <see cref="TraversalClient"/> on the candidate's own address family.
/// </summary>
public sealed class TraversalClientHolePunchTests
{
    [Fact]
    public async Task ConnectAsync_WhenCandidateIsIPv4_HolePunchesWithoutReflector()
    {
        using UdpClient candidate = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint candidateEndpoint = (IPEndPoint)candidate.Client.LocalEndPoint!;

        // The candidate answers the first probe it receives, which completes the hole punch.
        Task responder = Task.Run(async () =>
        {
            UdpReceiveResult probe = await candidate.ReceiveAsync();
            _ = await candidate.SendAsync(new byte[] { 1 }, probe.RemoteEndPoint);
        });

        FakeTraversalSession session = new();
        session.OnSend = packet =>
        {
            if (packet is PeerSignal request && request.Type == SignalType.Request)
            {
                session.Deliver(CreateIPv4CandidateOffer(request.TargetPeerId, candidateEndpoint));
            }
        };

        TraversalClient client = new(session);
        using TraversalSocket socket = await client.ConnectAsync(
            2UL,
            new IPEndPoint(IPAddress.Loopback, 9),
            TimeSpan.FromSeconds(30));

        await responder.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(socket.IsReflected);
    }

    private static PeerSignal CreateIPv4CandidateOffer(ulong targetPeerId, IPEndPoint candidate)
    {
        // The server carries addresses as 16 bytes; IPv4 is carried in its IPv4-mapped IPv6 form (::ffff:a.b.c.d).
        byte[] address = new byte[16];
        address[10] = 0xFF;
        address[11] = 0xFF;
        candidate.Address.GetAddressBytes().CopyTo(address, 12);

        return new PeerSignal
        {
            Type = SignalType.CandidateOffer,
            TargetPeerId = targetPeerId,
            AddressLow = BinaryPrimitives.ReadUInt64LittleEndian(address),
            AddressHigh = BinaryPrimitives.ReadUInt64LittleEndian(address.AsSpan(8)),
            Port = (ushort)candidate.Port,
        };
    }
}
