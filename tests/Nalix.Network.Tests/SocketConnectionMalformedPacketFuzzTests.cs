using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;
using TransportAsyncCallback = Nalix.Network.Internal.Transport.AsyncCallback;

namespace Nalix.Network.Tests;

/// <summary>
/// Area 2 (malformed packet/header fuzz) coverage: seeded-random blobs through the real receive
/// path (clean reject/close only, never crash/hang), header field mutation (unknown opcode, wrong
/// flags, declared/actual length mismatch), and opcode-handler exception containment
/// (<see cref="TransportAsyncCallback"/>'s EXECUTE_AND_RETURN try/catch around dispatched handlers).
/// Uses the same real-loopback-socket harness as <see cref="SocketConnectionFramingAdversarialTests"/>.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(AsyncCallbackSerialGroup.Name)]
public sealed class SocketConnectionMalformedPacketFuzzTests
{
    private const int Seed = 20260704;
    private const int Iterations = 500;

    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    private static byte[] CreateFrame(ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[payload.Length + sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);
        payload.CopyTo(frame.AsSpan(sizeof(ushort)));
        return frame;
    }

    // 6-byte application header: OpCode(0,2) Flags(2,1) Priority(3,1) SequenceId(4,2).
    private static byte[] CreateHeaderedPayload(ushort opCode, byte flags, byte priority, ushort sequenceId, ReadOnlySpan<byte> body)
    {
        byte[] payload = new byte[6 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), opCode);
        payload[2] = flags;
        payload[3] = priority;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), sequenceId);
        body.CopyTo(payload.AsSpan(6));
        return payload;
    }

    /// <summary>
    /// 500 seeded-random length-prefixed blobs of varying size (including undersized/oversized
    /// headers and garbage bodies) must never crash the process or hang the connection — each is
    /// either silently dropped or dispatched, and the connection must remain disposable afterward.
    /// </summary>
    [Fact]
    public async Task RandomBlobs_NeverCrashOrHangConnection()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        connection.MessageProcessing += (_, args) => args.Lease?.Dispose();

        connection.TCP.BeginReceive();

        System.Random rng = new(Seed);
        for (int i = 0; i < Iterations; i++)
        {
            int bodyLen = rng.Next(0, 64);
            byte[] body = new byte[bodyLen];
            rng.NextBytes(body);
            byte[] frame = CreateFrame(body);

            try
            {
                await scope.ClientSocket.SendAsync(frame);
            }
            catch (SocketException)
            {
                // Connection may have been closed by the server as a defensive reaction; that is
                // an acceptable clean outcome for this fuzz sweep — stop sending.
                break;
            }

            if (i % 50 == 0)
            {
                await Task.Delay(1);
            }
        }

        await Task.Delay(100);

        Action dispose = () => connection.Dispose();
        dispose.Should().NotThrow(
            $"seed={Seed}, iterations={Iterations}: random blobs must never crash or leave the connection in an undisposable state");
    }

    /// <summary>
    /// An unknown/unregistered opcode value must not crash the frame-dispatch pipeline — the
    /// connection must still process a subsequent well-formed frame with a different opcode.
    /// </summary>
    [Fact]
    public async Task UnknownOpCode_DoesNotCrashDispatch_ConnectionSurvives()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        // MessageProcessing fires per-frame at the Connection layer regardless of opcode — opcode
        // routing/handler-lookup happens further upstream (protocol dispatch). This test asserts
        // that an "unknown" opcode value does not crash the frame pipeline, and a subsequent frame
        // is still dispatched afterward (i.e. the connection is not wedged by an odd opcode).
        System.Collections.Concurrent.ConcurrentQueue<ushort> observedOpCodes = new();
        TaskCompletionSource<bool> secondFrameObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            try
            {
                ushort opCode = args.Lease is { } lease && lease.Length >= 2
                    ? BinaryPrimitives.ReadUInt16LittleEndian(lease.Span[..2])
                    : (ushort)0;
                observedOpCodes.Enqueue(opCode);
                if (observedOpCodes.Count >= 2)
                {
                    secondFrameObserved.TrySetResult(true);
                }
            }
            finally { args.Lease?.Dispose(); }
        };

        connection.TCP.BeginReceive();

        byte[] unknownOpFrame = CreateFrame(CreateHeaderedPayload(0xFFFF, 0, 0, 1, [1, 2]));
        await scope.ClientSocket.SendAsync(unknownOpFrame);
        await Task.Delay(50);

        byte[] knownOpFrame = CreateFrame(CreateHeaderedPayload(0x0001, 0, 0, 2, [3, 4]));
        await scope.ClientSocket.SendAsync(knownOpFrame);

        await secondFrameObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        observedOpCodes.Should().Contain(0xFFFF, "the unknown-opcode frame must still be dispatched to MessageProcessing without crashing")
            .And.Contain(0x0001, "a subsequent known-opcode frame must still be dispatched after an unknown-opcode frame");
    }

    /// <summary>
    /// Arbitrary/invalid flags and priority byte values must not crash header parsing or dispatch
    /// — the frame is delivered to MessageProcessing (dispatch does not validate flags semantics
    /// at this layer) and the connection remains usable.
    /// </summary>
    [Theory]
    [InlineData(0xFF, 0xFF)]
    [InlineData(0x00, 0xFF)]
    [InlineData(0xAA, 0x55)]
    public async Task ArbitraryFlagsAndPriority_DoesNotCrashDispatch(byte flags, byte priority)
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<int> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            try { processObserved.TrySetResult(args.Lease?.Length ?? -1); }
            finally { args.Lease?.Dispose(); }
        };

        connection.TCP.BeginReceive();

        byte[] frame = CreateFrame(CreateHeaderedPayload(0x0002, flags, priority, 7, [1, 2, 3, 4]));
        await scope.ClientSocket.SendAsync(frame);

        int receivedLength = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        receivedLength.Should().BeGreaterThan(0, $"flags=0x{flags:X2} priority=0x{priority:X2} must not prevent dispatch");
    }

    /// <summary>
    /// A wire length-prefix that declares more bytes than are actually sent before the socket is
    /// half-closed (declared/actual length mismatch) must be handled as a clean partial-frame
    /// wait, never a crash — closing the client leaves the connection to observe EOF/disconnect.
    /// </summary>
    [Fact]
    public async Task DeclaredLengthExceedsActualBytesSent_ThenHalfClose_ConnectionClosesCleanly()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) => args.Lease?.Dispose();
        connection.ConnectionClosed += (_, _) => closed.TrySetResult(true);

        connection.TCP.BeginReceive();

        // Declare a much larger frame than is actually sent, then shut down the client's send side.
        byte[] header = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 4096);
        await scope.ClientSocket.SendAsync(header);
        await scope.ClientSocket.SendAsync(Encoding.UTF8.GetBytes("only a few bytes"));
        scope.ClientSocket.Shutdown(SocketShutdown.Send);

        await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        Action dispose = () => connection.Dispose();
        dispose.Should().NotThrow("a declared-length/actual-bytes-sent mismatch followed by half-close must never crash or hang connection teardown");
    }

    /// <summary>
    /// An exception thrown inside a MessageProcessing subscriber (simulating a faulty opcode
    /// handler) must be contained by the dispatcher (AsyncCallback.EXECUTE_AND_RETURN's try/catch)
    /// — it must not crash the process, and the connection must remain usable for the next frame.
    /// </summary>
    [Fact]
    public async Task HandlerException_IsContained_ConnectionSurvivesForNextFrame()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<int> secondObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        connection.MessageProcessing += (_, args) =>
        {
            int n = System.Threading.Interlocked.Increment(ref callCount);
            try
            {
                if (n == 1)
                {
                    throw new InvalidOperationException("simulated faulty opcode handler");
                }
                secondObserved.TrySetResult(args.Lease?.Length ?? -1);
            }
            finally
            {
                args.Lease?.Dispose();
            }
        };

        connection.TCP.BeginReceive();

        byte[] first = CreateFrame(CreateHeaderedPayload(0x0003, 0, 0, 1, [1, 1, 1, 1]));
        byte[] second = CreateFrame(CreateHeaderedPayload(0x0003, 0, 0, 2, [2, 2, 2, 2]));
        await scope.ClientSocket.SendAsync(first);
        await Task.Delay(50);
        await scope.ClientSocket.SendAsync(second);

        int secondLength = await secondObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        secondLength.Should().BeGreaterThan(0, "a handler exception on the first frame must be contained by the dispatcher and not prevent the second frame from being processed");
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
