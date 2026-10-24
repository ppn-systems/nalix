// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Listeners.Udp;
using Nalix.Network.Options;
using Nalix.Network.RateLimiting;
using Nalix.Network.Tests.TestSupport;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class SocketConnectionFragmentedFrameRegressionTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    private static (Socket listener, Socket client, Socket server) CreateConnectedSocketPair()
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task<Socket> acceptTask = Task.Run(() => listener.Accept());
        client.Connect(IPAddress.Loopback, port);
        Socket server = acceptTask.GetAwaiter().GetResult();
        return (listener, client, server);
    }

    private static byte[] WriteFragmentPayloadToBuffer(SocketConnection socketConn, ushort streamId, ushort chunkIndex, ushort totalChunks, bool isLast, ReadOnlySpan<byte> body)
    {
        FieldInfo bufferField = typeof(SocketConnection).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] buffer = (byte[])bufferField.GetValue(socketConn)!;
        FragmentHeader header = new(streamId, chunkIndex, totalChunks, isLast);
        header.WriteTo(buffer);
        body.CopyTo(buffer.AsSpan(FragmentHeader.WireSize));
        return buffer;
    }

    [Fact]
    public void ProcessFrameFromBuffer_WhenOwnerRejectsFragmentedFrame_DisposesAssembledLeaseCompletely()
    {
        var (listener, client, server) = CreateConnectedSocketPair();
        try
        {
            Connection realConn = new(server, s_testOpCodeExtractor);
            FieldInfo backingField = typeof(Connection).GetField("_backing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object backing = backingField.GetValue(realConn)!;
            FieldInfo socketField = backing.GetType().GetField("Socket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            SocketConnection socketConn = (SocketConnection)socketField.GetValue(backing)!;

            // Force OnFrameReceived to return false by setting PendingProcessCallbacks above MaxPerConnectionPendingPackets
            // and attaching a non-inlineable handler.
            realConn.MessageProcessing += (_, _) => { };
            FieldInfo pendingField = backing.GetType().GetField("PendingProcessCallbacks", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            pendingField.SetValue(backing, 1_000_000);

            MethodInfo processFrameMethod = typeof(SocketConnection).GetMethod("PROCESS_FRAME_FROM_BUFFER", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo assemblerField = typeof(SocketConnection).GetField("_fragmentAssembler", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // Send chunk 0 of 2 via PROCESS_FRAME_FROM_BUFFER -> HANDLE_FRAGMENTED_FRAME_DIRECT
            _ = WriteFragmentPayloadToBuffer(socketConn, streamId: 1, chunkIndex: 0, totalChunks: 2, isLast: false, [1, 2, 3, 4]);
            processFrameMethod.Invoke(socketConn, [0, FragmentHeader.WireSize + 4]);

            // Inspect the internal AccumLease before chunk 1 completes reassembly
            FragmentAssembler assembler = (FragmentAssembler)assemblerField.GetValue(socketConn)!;
            FieldInfo streamsField = typeof(FragmentAssembler).GetField("_streams", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var streams = (System.Collections.IDictionary)streamsField.GetValue(assembler)!;
            object streamState = streams[(ushort)1]!;
            FieldInfo accumLeaseField = streamState.GetType().GetField("AccumLease", BindingFlags.Instance | BindingFlags.NonPublic)!;
            BufferLease accumLease = (BufferLease)accumLeaseField.GetValue(streamState)!;

            // Send chunk 1 of 2 (total assembled = 8 bytes >= PacketConstants.HeaderSize)
            _ = WriteFragmentPayloadToBuffer(socketConn, streamId: 1, chunkIndex: 1, totalChunks: 2, isLast: true, [5, 6, 7, 8]);
            processFrameMethod.Invoke(socketConn, [0, FragmentHeader.WireSize + 4]);

            // Because OnFrameReceived returned false, HANDLE_FRAGMENTED_FRAME_DIRECT must have disposed accumLease to RefCount 0
            FieldInfo refCountField = typeof(BufferLease).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int refCount = (int)refCountField.GetValue(accumLease)!;
            refCount.Should().Be(0, "when OnFrameReceived returns false, assembledLease must not have an extra Retain() leaving _refCount == 1");

            pendingField.SetValue(backing, 0);
            realConn.Dispose();
        }
        finally
        {
            client.Dispose();
            server.Dispose();
            listener.Dispose();
        }
    }

    [Fact]
    public async Task ProcessFrameFromBuffer_WithFragmentedAndVarIntFramesBelowHeaderSize_DropsFrames()
    {
        var (listener, client, server) = CreateConnectedSocketPair();
        try
        {
            using Connection connection = new(server, s_testOpCodeExtractor);
            int dispatchedCount = 0;
            connection.MessageProcessing += (_, _) =>
            {
                Interlocked.Increment(ref dispatchedCount);
            };

            FieldInfo backingField = typeof(Connection).GetField("_backing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object backing = backingField.GetValue(connection)!;
            FieldInfo socketField = backing.GetType().GetField("Socket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            SocketConnection socketConn = (SocketConnection)socketField.GetValue(backing)!;

            // 1. Test HANDLE_FRAGMENTED_FRAME_DIRECT with 3-byte reassembled payload (< PacketConstants.HeaderSize = 6)
            MethodInfo processFrameMethod = typeof(SocketConnection).GetMethod("PROCESS_FRAME_FROM_BUFFER", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _ = WriteFragmentPayloadToBuffer(socketConn, streamId: 7, chunkIndex: 0, totalChunks: 1, isLast: true, [0xAA, 0xBB, 0xCC]);
            processFrameMethod.Invoke(socketConn, [0, FragmentHeader.WireSize + 3]);

            // 2. Test PROCESS_VARINT_FRAME_FROM_BUFFER with 3-byte payload (< PacketConstants.HeaderSize = 6)
            MethodInfo processVarIntMethod = typeof(SocketConnection).GetMethod("PROCESS_VARINT_FRAME_FROM_BUFFER", BindingFlags.Instance | BindingFlags.NonPublic)!;
            processVarIntMethod.Invoke(socketConn, [0, 3]);

            await Task.Delay(100);

            Volatile.Read(ref dispatchedCount).Should().Be(0, $"reassembled fragmented and VarInt payloads smaller than PacketConstants.HeaderSize ({PacketConstants.HeaderSize}) must be dropped");
        }
        finally
        {
            client.Dispose();
            server.Dispose();
            listener.Dispose();
        }
    }
}
