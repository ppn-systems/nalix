// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Fragments;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;
using TransportAsyncCallback = Nalix.Network.Internal.Transport.AsyncCallback;

namespace Nalix.Network.Tests.Internal.Transport;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.AsyncCallback)]
public sealed class SocketConnectionFragmentationTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private static readonly FieldInfo s_backingField =
        typeof(Connection).GetField("_backing", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Connection._backing field was not found.");

    private static readonly FieldInfo s_backingSocketField =
        typeof(Nalix.Network.Internal.Connections.ConnectionBacking).GetField("Socket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ConnectionBacking.Socket field was not found.");

    private static readonly FieldInfo s_fragmentAssemblerField =
        typeof(Nalix.Network.Internal.Transport.SocketConnection).GetField("_fragmentAssembler", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SocketConnection._fragmentAssembler field was not found.");

    private static readonly FieldInfo s_openFragmentStreamsField =
        typeof(Nalix.Network.Internal.Transport.SocketConnection).GetField("_openFragmentStreams", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SocketConnection._openFragmentStreams field was not found.");

    private static object? GetFragmentAssembler(Connection connection)
    {
        object? backing = s_backingField.GetValue(connection);
        if (backing == null) return null;
        object? socketConnection = s_backingSocketField.GetValue(backing);
        return socketConnection is null ? null : s_fragmentAssemblerField.GetValue(socketConnection);
    }

    private static int GetOpenFragmentStreams(Connection connection)
    {
        object? backing = s_backingField.GetValue(connection);
        if (backing == null) return -1;
        object? socketConnection = s_backingSocketField.GetValue(backing);
        return socketConnection is null ? -1 : (int)s_openFragmentStreamsField.GetValue(socketConnection)!;
    }

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(System.ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    [Fact]
    public async Task BeginReceive_WithNonFragmentedFrame_DoesNotCreateFragmentAssembler()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<int> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            {
                processObserved.TrySetResult(args.Lease?.Length ?? -1);
            }
        };

        connection.TCP.BeginReceive();
        await scope.ClientSocket.SendAsync(CreateFrame([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));

        int receivedLength = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));

        receivedLength.Should().Be(10);
        GetFragmentAssembler(connection).Should().BeNull();
    }

    [Fact]
    public async Task BeginReceive_WithFragmentedFrame_CreatesFragmentAssemblerOnDemand()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<byte[]> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            {
                processObserved.TrySetResult(args.Lease?.Span.ToArray() ?? []);
            }
        };

        connection.TCP.BeginReceive();
        await scope.ClientSocket.SendAsync(CreateFrame(CreateFragmentPayload([9, 8, 7, 6, 5, 4])));

        byte[] receivedPayload = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));

        receivedPayload.Should().Equal([9, 8, 7, 6, 5, 4]);
        GetFragmentAssembler(connection).Should().NotBeNull();
    }

    [Fact]
    public async Task BeginReceive_WithDuplicateChunkZeroStreams_DoesNotLeakOpenFragmentStreamsQuota()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<byte[]> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) =>
        {
            {
                processObserved.TrySetResult(args.Lease?.Span.ToArray() ?? []);
            }
        };

        connection.TCP.BeginReceive();

        // Send 10 pairs of duplicate ChunkIndex=0 frames on the same StreamId (exceeding MaxPerConnectionOpenFragmentStreams=4).
        // Previously, each duplicate ChunkIndex=0 incremented _openFragmentStreams and threw InvalidDataException without decrementing,
        // permanently exhausting the connection's fragment stream quota.
        for (ushort streamId = 1; streamId <= 10; streamId++)
        {
            byte[] dupChunk0 = CreateFrame(CreateFragmentPayload(streamId, chunkIndex: 0, totalChunks: 2, isLast: false, [1, 2, 3]));
            await scope.ClientSocket.SendAsync(dupChunk0);
            await scope.ClientSocket.SendAsync(dupChunk0);
        }

        // Now send a valid 2-chunk fragmented stream (StreamId=99) and verify it still reassembles and dispatches cleanly.
        byte[] validChunk0 = CreateFrame(CreateFragmentPayload(streamId: 99, chunkIndex: 0, totalChunks: 2, isLast: false, [10, 20, 30]));
        byte[] validChunk1 = CreateFrame(CreateFragmentPayload(streamId: 99, chunkIndex: 1, totalChunks: 2, isLast: true, [40, 50, 60]));
        await scope.ClientSocket.SendAsync(validChunk0);
        await scope.ClientSocket.SendAsync(validChunk1);

        byte[] receivedPayload = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));

        receivedPayload.Should().Equal([10, 20, 30, 40, 50, 60]);
        GetOpenFragmentStreams(connection).Should().Be(0);
        FragmentAssembler? assembler = GetFragmentAssembler(connection) as FragmentAssembler;
        assembler.Should().NotBeNull();
        assembler!.OpenStreamCount.Should().Be(0);
    }

    private static byte[] CreateFrame(ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[payload.Length + sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);
        payload.CopyTo(frame.AsSpan(sizeof(ushort)));
        return frame;
    }

    private static byte[] CreateFragmentPayload(ReadOnlySpan<byte> body)
        => CreateFragmentPayload(streamId: 1, chunkIndex: 0, totalChunks: 1, isLast: true, body);

    private static byte[] CreateFragmentPayload(ushort streamId, ushort chunkIndex, ushort totalChunks, bool isLast, ReadOnlySpan<byte> body)
    {
        byte[] payload = new byte[FragmentHeader.WireSize + body.Length];
        FragmentHeader header = new(streamId, chunkIndex, totalChunks, isLast);
        header.WriteTo(payload);
        body.CopyTo(payload.AsSpan(FragmentHeader.WireSize));
        return payload;
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















