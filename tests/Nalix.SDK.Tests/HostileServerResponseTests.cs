using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Codec.DataFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Xunit;

namespace Nalix.SDK.Tests;

/// <summary>
/// Drives a raw TCP "server" that emits malformed/hostile frames at a real client
/// <see cref="TcpSession"/>, verifying the receive loop never hangs an awaiter forever
/// and never crashes on garbage input.
/// </summary>
[Collection(TestCollections.RealServer)]
public sealed class HostileServerResponseTests : IDisposable
{
    public HostileServerResponseTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    private static async Task<(TcpListener listener, Socket serverSide)> AcceptOneAsync(int port, TcpSession client)
    {
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        Task<Socket> acceptTask = listener.AcceptSocketAsync();
        await client.ConnectAsync();
        Socket serverSide = await acceptTask;
        return (listener, serverSide);
    }

    /// <summary>
    /// Writes a raw TCP frame: 2-byte little-endian total length header followed by
    /// <paramref name="payload"/>. When <paramref name="declaredLength"/> is supplied it
    /// overrides the header value to simulate a truncated/mismatched frame.
    /// </summary>
    private static async Task SendRawFrameAsync(Socket socket, byte[] payload, ushort? declaredLength = null)
    {
        ushort totalLen = declaredLength ?? (ushort)(2 + payload.Length);
        byte[] header = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(header, totalLen);
        await socket.SendAsync(header, SocketFlags.None);
        if (payload.Length > 0)
        {
            await socket.SendAsync(payload, SocketFlags.None);
        }
    }

    [Fact]
    public async Task TruncatedFrame_ClientDoesNotHangOrCrash()
    {
        int port = TestUtils.GetFreePort();
        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });

        Exception? observedError = null;
        client.OnError += (_, ex) => observedError = ex;

        var (listener, serverSide) = await AcceptOneAsync(port, client);
        try
        {
            // Declare a frame of 100 bytes but only send 6 bytes of body, then stop —
            // client's RECEIVE_EXACTLY_ASYNC should eventually fault (connection reset) on close.
            byte[] header = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(header, 100);
            await serverSide.SendAsync(header, SocketFlags.None);
            await serverSide.SendAsync(new byte[6], SocketFlags.None);

            // Close the connection without sending the rest of the declared frame.
            serverSide.Shutdown(SocketShutdown.Both);
            serverSide.Close();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (client.IsConnected && !cts.IsCancellationRequested)
            {
                await Task.Delay(50, cts.Token);
            }

            Assert.False(client.IsConnected, "Client should have detected disconnect on truncated frame, not hang.");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task UnknownOpcodeUnencryptedFrame_ClientIgnoresWithoutCrashing()
    {
        int port = TestUtils.GetFreePort();
        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });

        var (listener, serverSide) = await AcceptOneAsync(port, client);
        try
        {
            // Unencrypted frame with a bogus opcode (0xFFFF) and a full 6-byte header (no body).
            byte[] payload = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 0xFFFF); // OpCode
            payload[2] = 0; // Flags = NONE (unencrypted)
            payload[3] = 0; // Priority
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), 0); // SequenceId

            await SendRawFrameAsync(serverSide, payload);

            // Give the client's receive loop time to process — it must not crash the loop.
            await Task.Delay(500);

            // The connection should still be alive (bad opcode alone isn't fatal at the
            // transport layer; it is up to higher-level subscribers to ignore it).
            Assert.True(client.IsConnected);
        }
        finally
        {
            listener.Stop();
            serverSide.Dispose();
        }
    }

    [Fact]
    public async Task UndecryptableEncryptedFrame_ClientDisconnectsWithoutHangingOrCrashing()
    {
        int port = TestUtils.GetFreePort();
        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });

        Exception? observedError = null;
        client.OnError += (_, ex) => observedError = ex;

        var (listener, serverSide) = await AcceptOneAsync(port, client);
        try
        {
            // Header with ENCRYPTED flag set but garbage ciphertext — decryption/auth fails inside
            // FramePipeline.ProcessInbound (throwing variant), which TcpFrameReader treats as a
            // non-fatal receive-loop-ending error: the loop breaks and the session disconnects
            // cleanly, it does not hang the awaiter or crash the process.
            byte[] payload = new byte[64];
            new Random(12345).NextBytes(payload);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 1); // Arbitrary opcode
            payload[2] = (byte)Nalix.Abstractions.Networking.Packets.PacketFlags.ENCRYPTED;

            await SendRawFrameAsync(serverSide, payload);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (client.IsConnected && !cts.IsCancellationRequested)
            {
                await Task.Delay(50, cts.Token);
            }

            Assert.False(client.IsConnected, "Client should disconnect cleanly on undecryptable frame, not hang.");
            Assert.NotNull(observedError);
        }
        finally
        {
            listener.Stop();
            serverSide.Dispose();
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
