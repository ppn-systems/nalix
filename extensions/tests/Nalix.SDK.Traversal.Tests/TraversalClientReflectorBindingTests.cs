// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Nalix.SDK.Traversal.Tests.TestSupport;
using Nalix.Traversal.Packets;

namespace Nalix.SDK.Traversal.Tests;

/// <summary>
/// Verifies that <see cref="TraversalClient"/> only adopts a <see cref="ReflectorAllocated"/> that belongs to its own
/// pending request, so that a reflector token issued for another pair is never used.
/// </summary>
public sealed class TraversalClientReflectorBindingTests
{
    private const ulong TargetPeerId = 2UL;
    private const ulong OtherPeerId = 99UL;
    private const ulong ForeignToken = 0xBADBADUL;
    private const ulong OwnToken = 0x600D600DUL;

    [Fact]
    public async Task ConnectAsync_WhenAllocationForAnotherPeerArrivesFirst_DoesNotAdoptItsToken()
    {
        ulong token = await ConnectAndReturnTokenAsync(init =>
        [
            // A notification for a session with a different peer, sent before our own answer.
            new ReflectorAllocated { ReflectorToken = ForeignToken, Success = true, PeerId = OtherPeerId, RequestNonce = 0 },
            new ReflectorAllocated { ReflectorToken = OwnToken, Success = true, PeerId = TargetPeerId, RequestNonce = init.RequestNonce },
        ]);

        Assert.Equal(OwnToken, token);
    }

    [Fact]
    public async Task ConnectAsync_WhenAllocationAnswersAnotherRequest_DoesNotAdoptItsToken()
    {
        ulong token = await ConnectAndReturnTokenAsync(init =>
        [
            // Same target, but answers a different request (different nonce).
            new ReflectorAllocated { ReflectorToken = ForeignToken, Success = true, PeerId = TargetPeerId, RequestNonce = init.RequestNonce + 1 },
            new ReflectorAllocated { ReflectorToken = OwnToken, Success = true, PeerId = TargetPeerId, RequestNonce = init.RequestNonce },
        ]);

        Assert.Equal(OwnToken, token);
    }

    /// <summary>
    /// Runs ConnectAsync through the reflector fallback. <paramref name="answersFor"/> receives the ReflectorInit the client
    /// sent and returns the inbound allocations delivered, in order, before the client's wait completes.
    /// </summary>
    private static async Task<ulong> ConnectAndReturnTokenAsync(Func<ReflectorInit, ReflectorAllocated[]> answersFor)
    {
        using UdpClient candidate = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient reflector = new(new IPEndPoint(IPAddress.Loopback, 0));
        FakeTraversalSession session = new();
        TraversalClient client = new(session);

        session.OnSend = packet =>
        {
            switch (packet)
            {
                case PeerSignal request when request.Type == SignalType.Request:
                    session.Deliver(CreateCandidateOffer(request.TargetPeerId, candidate));
                    break;

                case ReflectorInit init:
                    foreach (ReflectorAllocated allocated in answersFor(init))
                    {
                        session.Deliver(allocated);
                    }
                    break;
            }
        };

        using TraversalSocket socket = await client.ConnectAsync(
            TargetPeerId,
            (IPEndPoint)reflector.Client.LocalEndPoint!,
            TimeSpan.FromSeconds(30));

        // The token is the first 8 bytes of every datagram sent through a reflected socket.
        _ = await socket.SendAsync(new byte[] { 1, 2, 3 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        UdpReceiveResult received = await reflector.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(socket.IsReflected);
        return BinaryPrimitives.ReadUInt64LittleEndian(received.Buffer);
    }

    private static PeerSignal CreateCandidateOffer(ulong targetPeerId, UdpClient candidate)
    {
        // An IPv4-mapped IPv6 address (::ffff:127.0.0.1), split into the two halves PeerSignal carries.
        byte[] address = new byte[16];
        address[10] = 0xFF;
        address[11] = 0xFF;
        address[12] = 127;
        address[15] = 1;

        return new PeerSignal
        {
            Type = SignalType.CandidateOffer,
            TargetPeerId = targetPeerId,
            AddressLow = BinaryPrimitives.ReadUInt64LittleEndian(address),
            AddressHigh = BinaryPrimitives.ReadUInt64LittleEndian(address.AsSpan(8)),
            Port = (ushort)((IPEndPoint)candidate.Client.LocalEndPoint!).Port,
        };
    }
}
