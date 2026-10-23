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

namespace Nalix.SDK.Tests;

[Collection(TestCollections.RealServer)]
public sealed class UdpIntegrityTests : IDisposable
{
    private readonly Bytes32 _serverPublicKey;

    public UdpIntegrityTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
            PacketRegistry.Build();
        TestUtils.SetupCertificate();
        _serverPublicKey = Bytes32.Parse(TestUtils.GetServerPublicKey());
    }

    [Fact]
    public async Task UdpSpoofing_WithInvalidXxHash32_IsDropped_WithoutDeductingTrust()
    {
        int port = TestUtils.GetFreePort();
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
    public async Task UdpRequestResponse_Succeeds()
    {
        int port = TestUtils.GetFreePort();
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
    public async Task UdpSpoofing_WithXxHash32OrZeroSecret_IsRejected()
    {
        int port = TestUtils.GetFreePort();
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

    [Fact]
    public void FramePipeline_TryProcessInbound_WhenDecompressFails_DoesNotLeakSequenceNumber()
    {
        byte[] secret = new byte[32];
        secret.AsSpan().Fill(0x42);

        // Craft an inner frame with COMPRESSED flag set, but invalid LZ4 payload
        byte[] malformedInner = new byte[PacketHeader.Size + 8];
        malformedInner[2] = (byte)PacketFlags.COMPRESSED; // Flags offset = 2
        // Set uncompressed length in LZ4 header to something invalid (negative or mismatched)
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(malformedInner.AsSpan(PacketHeader.Size, 4), 1024);

        using BufferLease plainLease = BufferLease.CopyFrom(malformedInner);
        IBufferLease encrypted = FrameCipher.EncryptFrame(plainLease, secret, seq: 99u, CipherSuiteType.Chacha20Poly1305);
        try
        {
            bool ok = FramePipeline.TryProcessInbound(ref encrypted, secret, CipherSuiteType.Chacha20Poly1305, out uint? seq);
            Assert.False(ok);
            Assert.Null(seq);
        }
        finally
        {
            encrypted.Dispose();
        }
    }

    [Fact]
    public void TcpFrameReader_WhenSequenceIsRejected_DoesNotUpdateSequenceCounterInFinally()
    {
        var seqOpts = Nalix.Environment.Configuration.ConfigurationManager.Instance.Get<Nalix.Environment.Options.SequenceOptions>();
        uint prevTcpWindow = seqOpts.TcpWindow;
        seqOpts.TcpWindow = 0;
        try
        {
            TransportOptions options = new() { Algorithm = CipherSuiteType.Chacha20Poly1305 };
            using TcpSession session = new(options);
            int delivered = 0;
            session.OnMessageReceived += (_, _) =>
            {
                delivered++;
            };

            Span<byte> secretBytes = stackalloc byte[32];
            secretBytes.Fill(0x33);
            session.State.Secret = new Bytes32(secretBytes);
            session.State.EncryptionEnabled = true;

            object reader = typeof(TcpSession).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            MethodInfo processNormal = reader.GetType().GetMethod("PROCESS_NORMAL_FRAME", BindingFlags.Instance | BindingFlags.NonPublic)!;
            SequenceCounter recvSeq = (SequenceCounter)reader.GetType().GetProperty("Sequence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(reader)!;

            byte[] validPlain = new byte[PacketHeader.Size + 4];

            using (BufferLease plain10 = BufferLease.CopyFrom(validPlain))
            {
                IBufferLease enc10 = FrameCipher.EncryptFrame(plain10, session.State.Secret.AsSpan(), seq: 10u, CipherSuiteType.Chacha20Poly1305);
                processNormal.Invoke(reader, [enc10]);
            }

            Assert.Equal(1, delivered);
            Assert.Equal(10u, recvSeq.Current());

            // Now send seq = 8. Since TcpWindow == 0 (strict ordering), IsValid(8, 0) returns false.
            // The rejected packet must NOT call UpdateTo(8) in the finally block!
            using (BufferLease plain8 = BufferLease.CopyFrom(validPlain))
            {
                IBufferLease enc8 = FrameCipher.EncryptFrame(plain8, session.State.Secret.AsSpan(), seq: 8u, CipherSuiteType.Chacha20Poly1305);
                processNormal.Invoke(reader, [enc8]);
            }

            Assert.Equal(1, delivered);
            Assert.True(recvSeq.IsValid(8u, window: 32), "Rejected packet must not mark bit in SequenceCounter via finally block.");
        }
        finally
        {
            seqOpts.TcpWindow = prevTcpWindow;
        }
    }

    [Fact]
    public async Task HandshakeAsync_WhenAllowTofuFalseAndNoServerPublicKey_ThrowsNetworkException()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            TransportOptions options = new()
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = string.Empty
            };
            PropertyInfo? allowTofuProp = typeof(TransportOptions).GetProperty("AllowTofu");
            Assert.NotNull(allowTofuProp);
            allowTofuProp!.SetValue(options, false);

            using TcpSession session = new(options);
            await session.ConnectAsync();

            await Assert.ThrowsAsync<Nalix.Abstractions.Exceptions.NetworkException>(() => session.HandshakeAsync().AsTask());
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public void WsFrameReader_HasFragmentAssembler_AndDisposesIt()
    {
        Type wsReaderType = typeof(TcpSession).Assembly.GetType("Nalix.SDK.Transport.Internal.Ws.WsFrameReader")!;
        Type stateType = typeof(TcpSession).Assembly.GetType("Nalix.SDK.Transport.SessionState")!;
        object state = Activator.CreateInstance(stateType)!;

        ConstructorInfo ctor = wsReaderType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
        Func<System.Net.WebSockets.ClientWebSocket> wsProvider = () => null!;
        Action<IBufferLease> onMsg = _ => { };
        Action<Exception, System.Net.WebSockets.ClientWebSocket?> onDisc = (_, _) => { };
        using IDisposable reader = (IDisposable)ctor.Invoke([wsProvider, new TransportOptions(), state, new WebSocketTransportOptions(), onMsg, onDisc]);

        FieldInfo? assemblerField = wsReaderType.GetField("_assembler", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(assemblerField);

        object? assembler = assemblerField!.GetValue(reader);
        Assert.NotNull(assembler);

        reader.Dispose();

        FieldInfo? disposedField = assembler!.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(disposedField);
        Assert.True((bool)disposedField!.GetValue(assembler)!);
    }

    public void Dispose() => InstanceManager.Instance.Clear(dispose: false);
}

