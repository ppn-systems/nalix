using System;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Memory;
using Nalix.Hosting.Protocols;
using Nalix.Network.Connections;
using NSubstitute;
using Xunit;

namespace Nalix.Hosting.Tests;

public sealed class DefaultFrameProcessorReplayTests
{
    private static void EnsurePoolConfigured()
    {
        try
        {
            var config = new Nalix.Framework.Options.ObjectPoolOptions { EnableMetrics = false, EnableDiagnostics = false };
            var poolManager = new Nalix.Framework.Memory.Objects.ObjectPoolManager(config);
            Nalix.Framework.Memory.Objects.ObjectPoolManager.Configure(poolManager);
            Nalix.Framework.Injection.InstanceManager.Instance.Register<IObjectPoolManager>(poolManager);
            Nalix.Framework.Injection.InstanceManager.Instance.Register<Nalix.Framework.Memory.Objects.ObjectPoolManager>(poolManager);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Fact]
    public void ProcessFrame_WhenSequenceIsRejected_DoesNotUpdateSequenceCounterInFinally()
    {
        EnsurePoolConfigured();

        var seqOpts = Nalix.Environment.Configuration.ConfigurationManager.Instance.Get<Nalix.Environment.Options.SequenceOptions>();
        uint prevTcpWindow = seqOpts.TcpWindow;
        seqOpts.TcpWindow = 0;
        try
        {
            IProtocol protocol = Substitute.For<IProtocol>();
            DefaultFrameProcessor processor = new(NullLogger.Instance, protocol);

            using System.Net.Sockets.TcpListener tcpListener = new(System.Net.IPAddress.Loopback, 0);
            tcpListener.Start();
            int port = ((System.Net.IPEndPoint)tcpListener.LocalEndpoint).Port;
            using System.Net.Sockets.Socket clientSocket = new(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            clientSocket.Connect(System.Net.IPAddress.Loopback, port);
            using System.Net.Sockets.Socket serverSocket = tcpListener.AcceptSocket();
            tcpListener.Stop();

            using Connection connection = new(serverSocket, Substitute.For<Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor>());

            Span<byte> secret = stackalloc byte[32];
            secret.Fill(0x7A);
            connection.Secret = new Bytes32(secret);
            connection.Algorithm = CipherSuiteType.Chacha20Poly1305;

            MethodInfo acquireMethod = typeof(Connection).GetMethod("AcquireEventArgs", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo initMethod = typeof(ConnectionEventArgs).GetMethod(
                "Initialize",
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(IBufferLease), typeof(IConnection)])!;

            byte[] validPlain = new byte[PacketHeader.Size + 4];

            // 1. Process valid packet with seq = 10
            using (BufferLease plain10 = BufferLease.CopyFrom(validPlain))
            {
                IBufferLease enc10 = FrameCipher.EncryptFrame(plain10, secret, seq: 10u, CipherSuiteType.Chacha20Poly1305);
                enc10.IsReliable = true;
                ConnectionEventArgs args10 = (ConnectionEventArgs)acquireMethod.Invoke(connection, null)!;
                initMethod.Invoke(args10, [enc10, connection]);
                try
                {
                    processor.ProcessFrame(this, args10);
                }
                finally
                {
                    args10.Dispose();
                }
            }

            Assert.Equal(10u, connection.TCP.ReceiveSequence.Current());

            // 2. Process out-of-order packet with seq = 8 (rejected when TcpWindow == 0).
            // Because IsValid(8, window: 0) returns false, UpdateTo(8) must NOT be called in finally!
            using (BufferLease plain8 = BufferLease.CopyFrom(validPlain))
            {
                IBufferLease enc8 = FrameCipher.EncryptFrame(plain8, secret, seq: 8u, CipherSuiteType.Chacha20Poly1305);
                enc8.IsReliable = true;
                ConnectionEventArgs args8 = (ConnectionEventArgs)acquireMethod.Invoke(connection, null)!;
                initMethod.Invoke(args8, [enc8, connection]);
                try
                {
                    processor.ProcessFrame(this, args8);
                }
                finally
                {
                    args8.Dispose();
                }
            }

            Assert.True(connection.TCP.ReceiveSequence.IsValid(8u, window: 32));
        }
        finally
        {
            seqOpts.TcpWindow = prevTcpWindow;
        }
    }
}
