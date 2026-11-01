// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;
using Nalix.Network.Connections;
using Nalix.Network.Listeners.Udp;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Audit.UdpSession;

/// <summary>
/// Audit reproductions for the authenticated UDP datagram tag (NETUDP-03).
/// The tag is <c>XxHash32(secret, seed: XxHash32(datagram))</c>, which is an unkeyed 32-bit hash and not a MAC.
/// </summary>
[Trait("Category", "Audit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class UdpDatagramTagAuditTests
{
    /// <summary>Drives the production <see cref="UdpListenerBase.ProcessDatagram"/> path with a known connection.</summary>
    private sealed class AuditUdpListener : UdpListenerBase
    {
        private readonly Connection _target;

        public AuditUdpListener(IProtocol protocol, IConnectionHub hub, Connection target) : base(0, protocol, hub)
            => _target = target;

        public override bool IsAuthenticated(IConnection connection, EndPoint remoteEndPoint, ReadOnlySpan<byte> payload) => true;

        public override bool TryResolveConnection(IConnectionHub hub, ReadOnlySpan<byte> sessionToken, out Connection? connection)
        {
            connection = _target;
            return true;
        }

        public void Inject(BufferLease lease, EndPoint endPoint) => this.ProcessDatagram(lease, endPoint);
    }

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private static readonly IPEndPoint s_remote = new(IPAddress.Loopback, 19000);

    [Fact]
    public void ProcessDatagram_GenuineTaggedDatagram_IsAccepted()
    {
        // Control: proves the harness and the tag formula are correct. This passes on the current code.
        using Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using Connection connection = NewConnection(tcp);
        using AuditUdpListener listener = NewListener(connection);

        byte[] data = BuildData(connection.ConnectionId, [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]);
        byte[] genuine = Append(data, ComputeTag(connection.Secret, data));

        listener.Inject(BufferLease.CopyFrom(genuine), s_remote);

        listener.Metrics.ReceivedPackets.Should().Be(1, "a datagram carrying the correct tag must be accepted");
        listener.Metrics.DroppedUnauth.Should().Be(0);
    }

    /// <summary>
    /// NETUDP-03: an attacker who observed one genuine datagram (plaintext on UDP) knows its tag.
    /// Because the tag depends only on the 32-bit XxHash32 digest of the datagram and not on a keyed MAC over it,
    /// a different payload with the same digest carries the same tag and is accepted. No secret is needed to build it.
    /// </summary>
    [Fact]
    public void ProcessDatagram_ForgedPayloadWithTagOfObservedDatagramWithSameXxHash32_IsRejected()
    {
        using Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using Connection connection = NewConnection(tcp);
        using AuditUdpListener listener = NewListener(connection);

        ulong token = connection.ConnectionId;
        byte[] header = new byte[PacketHeader.Size];

        (byte[] genuineData, byte[] forgedData) = FindXxHash32Collision(token, header);

        // The attacker observes the genuine datagram on the wire and keeps its tag.
        byte[] observedTag = ComputeTag(connection.Secret, genuineData);
        byte[] forgedDatagram = Append(forgedData, observedTag);

        listener.Inject(BufferLease.CopyFrom(forgedDatagram), s_remote);

        listener.Metrics.ReceivedPackets.Should().Be(
            0,
            "a payload that was never authenticated by the sender must not be accepted with a reused 4-byte tag");
        listener.Metrics.DroppedUnauth.Should().Be(1);
    }

    /// <summary>
    /// Searches for two distinct datagrams with the same 32-bit XxHash32 digest. The search is deterministic
    /// (fixed seed) and bounded. This pair stands in for the attacker's search: a real attacker must find a payload
    /// whose digest matches an observed datagram (a second preimage, roughly 2^32 offline XxHash32 evaluations, no secret
    /// needed). The harness uses a birthday pair (roughly 10^5 trials) only to make the reproduction fast.
    /// </summary>
    private static (byte[] Genuine, byte[] Forged) FindXxHash32Collision(ulong token, byte[] header)
    {
        Dictionary<uint, byte[]> seen = new();
        Random rng = new(20260101);
        byte[] payload = new byte[8];

        for (int i = 0; i < 4_000_000; i++)
        {
            rng.NextBytes(payload);
            byte[] data = BuildData(token, header, payload);
            uint digest = XxHash32.Compute(data);

            if (seen.TryGetValue(digest, out byte[]? other))
            {
                return (other, data);
            }

            seen[digest] = data;
        }

        throw new InvalidOperationException("No XxHash32 collision was found within the search bound.");
    }

    private static byte[] ComputeTag(Bytes32 secret, ReadOnlySpan<byte> data)
    {
        // Mirrors UdpListenerBase.ProcessDatagram: tag = XxHash32(secret, seed: XxHash32(data)).
        uint digest = XxHash32.Compute(data);
        uint tag = XxHash32.Compute(secret.AsSpan(), seed: digest);

        byte[] bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, tag);
        return bytes;
    }

    private static byte[] BuildData(ulong token, ReadOnlySpan<byte> payload) =>
        BuildData(token, new byte[PacketHeader.Size], payload);

    private static byte[] BuildData(ulong token, byte[] header, ReadOnlySpan<byte> payload)
    {
        byte[] data = new byte[sizeof(ulong) + header.Length + payload.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(data, token);
        header.CopyTo(data, sizeof(ulong));
        payload.CopyTo(data.AsSpan(sizeof(ulong) + header.Length));
        return data;
    }

    private static byte[] Append(byte[] data, byte[] tail)
    {
        byte[] result = new byte[data.Length + tail.Length];
        data.CopyTo(result, 0);
        tail.CopyTo(result, data.Length);
        return result;
    }

    private static Connection NewConnection(Socket tcp)
    {
        Connection connection = new(tcp, new TestOpCodeExtractor(), s_remote);
        byte[] secret = new byte[32];
        for (int i = 0; i < secret.Length; i++)
        {
            secret[i] = (byte)(i + 1);
        }
        connection.Secret = new Bytes32(secret);
        return connection;
    }

    private static AuditUdpListener NewListener(Connection connection)
    {
        IProtocol protocol = Substitute.For<IProtocol>();
        protocol.OpCodeExtractor.Returns(new TestOpCodeExtractor());
        IConnectionHub hub = Substitute.For<IConnectionHub>();

        AuditUdpListener listener = new(protocol, hub, connection);

        // EnsureUDP needs the listener's bound socket. Bind a dummy UDP socket, as the passthrough tests do.
        Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        FieldInfo socketField = typeof(UdpListenerBase).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UdpListenerBase._socket not found.");
        socketField.SetValue(listener, udp);

        return listener;
    }
}
