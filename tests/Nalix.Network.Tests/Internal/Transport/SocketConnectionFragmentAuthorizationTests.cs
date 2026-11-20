// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Environment.Fragments;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;
using TransportAsyncCallback = Nalix.Network.Internal.Transport.AsyncCallback;

namespace Nalix.Network.Tests.Internal.Transport;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.AsyncCallback)]
public sealed class SocketConnectionFragmentAuthorizationTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private static readonly byte[] s_sentinelPayload = [0xEE, 0xEE, 1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly FieldInfo s_backingField =
        typeof(Connection).GetField("_backing", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Connection._backing field was not found.");

    private static readonly FieldInfo s_backingSocketField =
        typeof(Nalix.Network.Internal.Connections.ConnectionBacking).GetField("Socket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ConnectionBacking.Socket field was not found.");

    private static readonly FieldInfo s_fragmentAssemblerField =
        typeof(SocketConnection).GetField("_fragmentAssembler", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SocketConnection._fragmentAssembler field was not found.");

    public static TheoryData<PermissionLevel> BelowEstablishedLevels => new()
    {
        PermissionLevel.NONE,
        PermissionLevel.POW_VERIFIED,
    };

    [Theory]
    [MemberData(nameof(BelowEstablishedLevels))]
    public async Task BeginReceive_WhenPermissionBelowEstablished_DropsFragmentedFrame(PermissionLevel level)
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        connection.Level = level;
        TransportAsyncCallback.ResetStatistics();

        IReadOnlyList<byte[]> dispatched = await SendThenAwaitSentinelAsync(
            connection,
            scope,
            [CreateFrame(CreateFragmentPayload(streamId: 1, chunkIndex: 0, totalChunks: 1, isLast: true, [9, 8, 7, 6, 5, 4]))]);

        dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task BeginReceive_WhenConnectionNotAuthenticated_DoesNotRetainReassemblyBuffer()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        TransportAsyncCallback.ResetStatistics();

        IReadOnlyList<byte[]> dispatched = await SendThenAwaitSentinelAsync(
            connection,
            scope,
            [
                CreateFrame(CreateFragmentPayload(streamId: 1, chunkIndex: 0, totalChunks: 2, isLast: false, [1, 2, 3, 4])),
                CreateFrame(CreateFragmentPayload(streamId: 1, chunkIndex: 1, totalChunks: 2, isLast: true, [5, 6, 7, 8])),
            ]);

        dispatched.Should().BeEmpty();
        GetFragmentAssembler(connection).Should().BeNull();
    }

    [Fact]
    public async Task BeginReceive_WhenConnectionEstablished_ReassemblesFragmentedFrame()
    {
        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, s_testOpCodeExtractor);
        connection.Level = PermissionLevel.ESTABLISHED;
        TransportAsyncCallback.ResetStatistics();

        TaskCompletionSource<byte[]> processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, args) => processObserved.TrySetResult(args.Lease?.Span.ToArray() ?? []);

        connection.Transport.BeginReceive();
        await scope.ClientSocket.SendAsync(CreateFrame(CreateFragmentPayload(streamId: 2, chunkIndex: 0, totalChunks: 2, isLast: false, [9, 8, 7])));
        await scope.ClientSocket.SendAsync(CreateFrame(CreateFragmentPayload(streamId: 2, chunkIndex: 1, totalChunks: 2, isLast: true, [6, 5, 4])));

        byte[] receivedPayload = await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));

        receivedPayload.Should().Equal([9, 8, 7, 6, 5, 4]);
        GetFragmentAssembler(connection).Should().NotBeNull();
    }

    private static async Task<IReadOnlyList<byte[]>> SendThenAwaitSentinelAsync(Connection connection, ConnectedSocketScope scope, byte[][] frames)
    {
        ConcurrentQueue<byte[]> dispatched = new();
        TaskCompletionSource<bool> sentinelObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.MessageProcessing += (_, args) =>
        {
            byte[] payload = args.Lease?.Span.ToArray() ?? [];
            if (payload.AsSpan().SequenceEqual(s_sentinelPayload))
            {
                sentinelObserved.TrySetResult(true);
                return;
            }

            dispatched.Enqueue(payload);
        };

        connection.Transport.BeginReceive();
        foreach (byte[] frame in frames)
        {
            await scope.ClientSocket.SendAsync(frame);
        }

        // TCP preserves order and the receive loop is sequential, so once the sentinel is dispatched
        // every frame sent before it has already been processed by the receive path.
        await scope.ClientSocket.SendAsync(CreateFrame(s_sentinelPayload));
        await sentinelObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));

        return [.. dispatched];
    }

    private static object? GetFragmentAssembler(Connection connection)
    {
        object? backing = s_backingField.GetValue(connection);
        if (backing == null)
        {
            return null;
        }

        object? socketConnection = s_backingSocketField.GetValue(backing);
        return socketConnection is null ? null : s_fragmentAssemblerField.GetValue(socketConnection);
    }

    private static byte[] CreateFrame(ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[payload.Length + sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)frame.Length);
        payload.CopyTo(frame.AsSpan(sizeof(ushort)));
        return frame;
    }

    private static byte[] CreateFragmentPayload(ushort streamId, ushort chunkIndex, ushort totalChunks, bool isLast, ReadOnlySpan<byte> body)
    {
        byte[] payload = new byte[FragmentHeader.WireSize + body.Length];
        FragmentHeader header = new(streamId, chunkIndex, totalChunks, isLast);
        header.WriteTo(payload);
        body.CopyTo(payload.AsSpan(FragmentHeader.WireSize));
        return payload;
    }

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload) : (ushort)0;
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
            ClientSocket.Dispose();
            ServerSocket.Dispose();
            ListenerSocket.Dispose();
        }
    }
}
