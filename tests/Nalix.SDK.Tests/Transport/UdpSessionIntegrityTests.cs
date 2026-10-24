// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport;

[Collection(TestCollections.RealServer)]
public sealed class UdpSessionIntegrityTests : IDisposable
{
    private readonly Bytes32 _serverPublicKey;

    public UdpSessionIntegrityTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
            PacketRegistry.Build();
        TestEnvironment.SetupCertificate();
        _serverPublicKey = Bytes32.Parse(TestEnvironment.GetServerPublicKey());
    }

    [Fact]
    public async Task UdpDatagram_WithInvalidXxHash32_IsDroppedWithoutKickingClient()
    {
        int port = TestEnvironment.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.ListenUdp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();
        builder.UseTimeSync();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using TcpSession session = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = _serverPublicKey.ToString()
            });

            await session.ConnectAsync();
            await session.HandshakeAsync();

            Assert.True(session.State.EncryptionEnabled);

            // Now simulate an attacker spoofing UDP packets
            // Attacker knows the SessionToken (which is visible in plaintext UDP headers)
            // But attacker does NOT know the Connection.Secret.
            using UdpClient attacker = new();
            
            // Craft a fake datagram: [SessionToken (8)] + [Payload (N)] + [FakeHash (4)]
            byte[] fakePacket = new byte[8 + 10 + 4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(fakePacket.AsSpan(0, 8), session.State.SessionToken);
            
            // Attacker sends 20 spoofed packets
            for (int i = 0; i < 20; i++)
            {
                await attacker.SendAsync(fakePacket, fakePacket.Length, "127.0.0.1", port);
            }

            // Give the server time to process
            await Task.Delay(500);

            // If the XxHash32 drop logic failed, the server would have processed these as invalid encrypted frames,
            // resulting in Trust Score deduction, and after 10 errors, it would kick the legitimate client!
            // Let's verify the legitimate client is STILL CONNECTED by sending a ping.
            var pingResult = await session.PingAsync();
            Assert.True(pingResult >= 0);
            Assert.True(session.IsConnected);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task PingAsync_OverUdp_Succeeds()
    {
        int port = TestEnvironment.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.ListenUdp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();
        builder.UseTimeSync();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using TcpSession tcpSession = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = _serverPublicKey.ToString()
            });

            await tcpSession.ConnectAsync();
            await tcpSession.HandshakeAsync();

            Assert.True(tcpSession.State.EncryptionEnabled);

            using UdpSession udpSession = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port
            }, tcpSession.State);

            await udpSession.ConnectAsync();

            // Perform UDP Ping (request/response)
            double rtt = await udpSession.PingAsync(timeoutMs: 3000);
            Assert.True(rtt >= 0);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task UdpDatagram_WithConcatenatedXxHash32Tag_IsRejected()
    {
        int port = TestEnvironment.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.ListenUdp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();
        builder.UseTimeSync();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using TcpSession tcpSession = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = _serverPublicKey.ToString()
            });

            await tcpSession.ConnectAsync();
            await tcpSession.HandshakeAsync();

            // Craft a UDP datagram whose outer tag is computed with unseeded concatenated XxHash32(data || Secret)
            // and whose inner frame has seq = 500.
            // On unpatched code (concatenated XxHash32), the server accepts it and advances UDP ReceiveSequence to 500,
            // causing a subsequent legitimate udpSession.PingAsync() (seq = 1) to be dropped as old/replay!
            // On patched code (2-step seeded XxHash32), the concatenated-tagged packet is rejected at UdpListener,
            // so udpSession.PingAsync() (seq = 1) succeeds.
            TimeSync ping500 = new();
            ping500.Initialize(Nalix.Abstractions.Networking.Protocols.ControlType.PING, 7777, PacketFlags.NONE);
            byte[] rawPing = ping500.Serialize();
            IBufferLease payloadLease = BufferLease.CopyFrom(rawPing);
            FramePipeline.ProcessOutbound(
                ref payloadLease,
                enableCompress: false,
                minSizeToCompress: 512,
                enableEncrypt: true,
                secret: tcpSession.State.Secret.AsSpan(),
                seq: 500,
                algorithm: CipherSuiteType.Chacha20Poly1305);

            int dataLen = 8 + payloadLease.Length;
            byte[] forgedPacket = new byte[dataLen + 32];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(forgedPacket.AsSpan(0, 8), tcpSession.State.SessionToken);
            payloadLease.Span.CopyTo(forgedPacket.AsSpan(8));
            tcpSession.State.Secret.AsSpan().CopyTo(forgedPacket.AsSpan(dataLen));
            uint xxHash = XxHash32.Compute(forgedPacket.AsSpan(0, dataLen + 32));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(forgedPacket.AsSpan(dataLen, 4), xxHash);
            payloadLease.Dispose();

            using (UdpClient rawUdp = new())
            {
                await rawUdp.SendAsync(forgedPacket.AsMemory(0, dataLen + 4), new IPEndPoint(IPAddress.Loopback, port));
            }

            await Task.Delay(200);

            using UdpSession udpSession = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port
            }, tcpSession.State);
            await udpSession.ConnectAsync();

            double rtt = await udpSession.PingAsync(timeoutMs: 800);
            Assert.True(rtt >= 0);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose() => InstanceManager.Instance.Clear(dispose: false);
}
