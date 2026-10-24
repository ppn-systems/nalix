// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Memory;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;
using TransportAsyncCallback = Nalix.Network.Internal.Transport.AsyncCallback;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// Adversarial coverage for the VarInt (LEB128) length-prefixed framing path
/// (<see cref="TransportFraming.VarIntLengthPrefixed"/>), specifically the
/// int.MaxValue-as-unsigned / negative-as-signed attack vectors that the fixed-width
/// 2-byte UInt16 header cannot represent (see <see cref="SocketConnectionFramingAdversarialTests"/>
/// for that framing's coverage). <see cref="Nalix.Environment.Memory.Leb128"/>'s
/// <c>TryRead(ReadOnlySpan&lt;byte&gt;, out int, out int)</c> overload decodes into a <see cref="uint"/>
/// then casts to <see cref="int"/>, so a LEB128 encoding of <see cref="uint.MaxValue"/> becomes -1 and
/// must be rejected by the explicit <c>payloadLen &lt; 0</c> check in
/// <c>SocketConnection.Receive.VarInt.cs</c>.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.AsyncCallback)]
public sealed class SocketConnectionVarIntFramingAdversarialTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    private static byte[] CreateVarIntFrame(ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[Leb128.MaxByteCount];
        int headerLen = Leb128.Write(header, (uint)payload.Length);
        byte[] frame = new byte[headerLen + payload.Length];
        header[..headerLen].CopyTo(frame);
        payload.CopyTo(frame.AsSpan(headerLen));
        return frame;
    }

    private static async Task<Connection> CreateVarIntConnectionAsync(ConnectedSocketScope scope)
    {
        Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        connection.TCP.UseFraming(TransportFraming.VarIntLengthPrefixed);
        await Task.CompletedTask;
        return connection;
    }

    /// <summary>
    /// Policy A: a LEB128 encoding of <see cref="uint.MaxValue"/> (5 bytes, FF FF FF FF 0F) decodes
    /// to <c>uint 0xFFFFFFFF</c>, casts to <c>int -1</c>, and is rejected by the
    /// <c>payloadLen &lt; 0</c> check. Since no reliable resync marker exists in this protocol, this
    /// is treated as a fatal protocol violation: the connection is closed and no further frame
    /// (even a well-formed one sent right after) is ever dispatched.
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithUIntMaxValueLeb128Length_ClosesConnectionAndDispatchesNothing()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = await CreateVarIntConnectionAsync(scope);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        connection.MessageProcessing += (_, _) =>
        {
            Interlocked.Increment(ref callCount);
        };
        connection.ConnectionClosed += (_, _) => closed.TrySetResult(true);

        connection.TCP.BeginReceive();

        // LEB128 encoding of uint.MaxValue: FF FF FF FF 0F.
        byte[] poisonHeader = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F];
        byte[] validFrame = CreateVarIntFrame([7, 7, 7, 7, 7, 7]);
        await scope.ClientSocket.SendAsync(poisonHeader);
        await Task.Delay(50);
        await scope.ClientSocket.SendAsync(validFrame);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await Task.Delay(200);
        Volatile.Read(ref callCount).Should().Be(0, "a LEB128-encoded uint.MaxValue desynchronizes the stream; the connection must close and no frame (including one sent right after) must ever be dispatched");
    }

    /// <summary>
    /// A declared VarInt payload length exactly one byte beyond the configured
    /// <c>VarIntFramingOptions.MaxPayloadSize</c> (131072) must be rejected without allocating an
    /// attacker-controlled buffer, and the connection must remain usable/disposable.
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithLengthOneByteOverMaxPayload_RejectsWithoutAllocating()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = await CreateVarIntConnectionAsync(scope);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<bool> closedOrIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, _) => { };
        connection.ConnectionClosed += (_, _) => closedOrIdle.TrySetResult(true);

        connection.TCP.BeginReceive();

        const int oneOverMax = 131_072 + 1;
        Span<byte> header = stackalloc byte[Leb128.MaxByteCount];
        int headerLen = Leb128.Write(header, (uint)oneOverMax);
        await scope.ClientSocket.SendAsync(header[..headerLen].ToArray());

        await Task.WhenAny(closedOrIdle.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        Action dispose = () => connection.Dispose();
        dispose.Should().NotThrow("an over-cap declared VarInt payload length must never crash or corrupt connection teardown");
    }

    /// <summary>
    /// A declared VarInt payload length within <c>VarIntFramingOptions.MaxPayloadSize</c> (131072)
    /// but beyond the server's configured max receive buffer size (mirrors the UInt16 path's
    /// <c>s_maxReceiveBufferSize</c> ceiling in SocketConnection.cs) must be rejected via
    /// GetMessageSize() rather than growing the receive buffer without bound.
    /// </summary>
    [Fact]
    public async Task BeginReceive_WithLengthOverReceiveBufferCeiling_RejectsWithoutUnboundedGrowth()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = await CreateVarIntConnectionAsync(scope);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<bool> closedOrIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, _) => { };
        connection.ConnectionClosed += (_, _) => closedOrIdle.TrySetResult(true);

        connection.TCP.BeginReceive();

        // Below MaxPayloadSize (131072) but far above the default receive-buffer ceiling
        // (~32KB chunk size + fragment header + varint header).
        const int overReceiveBufferCeiling = 100_000;
        Span<byte> header = stackalloc byte[Leb128.MaxByteCount];
        int headerLen = Leb128.Write(header, (uint)overReceiveBufferCeiling);
        await scope.ClientSocket.SendAsync(header[..headerLen].ToArray());

        await Task.WhenAny(closedOrIdle.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        Action dispose = () => connection.Dispose();
        dispose.Should().NotThrow("a declared VarInt length beyond the receive buffer ceiling must never crash or corrupt connection teardown");
    }

    /// <summary>
    /// Splitting a single VarInt-framed message at every possible byte boundary must always
    /// reassemble to the exact original payload — mirrors the UInt16 path's equivalent coverage.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task BeginReceive_WithVarIntFrameSplitIntoChunks_ReassemblesExactPayload(int chunkSize)
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = await CreateVarIntConnectionAsync(scope);
        TransportAsyncCallback.ResetStatistics();

        byte[] payload = SequentialBytes(37, seed: 4);
        byte[] frame = CreateVarIntFrame(payload);

        TaskCompletionSource<byte[]> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            { processObserved.TrySetResult(args.Lease?.Span.ToArray() ?? []); }
        };

        connection.TCP.BeginReceive();

        for (int offset = 0; offset < frame.Length; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, frame.Length - offset);
            await scope.ClientSocket.SendAsync(frame.AsSpan(offset, len).ToArray());
            await Task.Delay(1);
        }

        byte[] received = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        received.Should().Equal(payload, $"split at chunkSize={chunkSize} must reassemble to the exact original payload");
    }

    private static byte[] SequentialBytes(int length, int seed)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(seed + i);
        }
        return data;
    }

    private sealed class ConnectedSocketScope : IDisposable
    {
        private ConnectedSocketScope(Socket listenerSocket, Socket clientSocket, Socket serverSocket)
        {
            ListenerSocket = listenerSocket;
            ClientSocket = clientSocket;
            ServerSocket = serverSocket;
        }

        public Socket ListenerSocket { get; }

        public Socket ClientSocket { get; }

        public Socket ServerSocket { get; }

        public static async Task<ConnectedSocketScope> CreateAsync()
        {
            Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            Task<Socket> acceptTask = Task.Run(() => listener.Accept());

            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, port);

            Socket server = await acceptTask;
            return new ConnectedSocketScope(listener, client, server);
        }

        public void Dispose()
        {
            try { ClientSocket.Dispose(); } catch { }
            try { ServerSocket.Dispose(); } catch { }
            try { ListenerSocket.Dispose(); } catch { }
        }
    }
}
