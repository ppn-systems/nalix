// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Memory;
using Nalix.Hosting.Protocols;
using Nalix.Network.Protocols;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Handlers;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;

namespace Nalix.SDK.Tests.TestSupport;

internal sealed class FakeSession(bool isConnected) : TransportSession
{
    private readonly FakePacketRegistry _catalog = new();
    public override TransportOptions Options { get; } = new();
    public override bool IsConnected { get; } = isConnected;
    public int SendPacketCallCount { get; private set; }

    // Required overrides; test code subscribes via SubscribeTemp / On<T> extension methods.
#pragma warning disable CS0067 // Event is never used
    public override event EventHandler? OnConnected;
    public override event EventHandler<Exception>? OnDisconnected;
    public override event EventHandler<IBufferLease>? OnMessageReceived;
    public override event EventHandler<Exception>? OnError;
#pragma warning restore CS0067

    public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default) => Task.CompletedTask;
    public override Task DisconnectAsync() => Task.CompletedTask;

    public override Task SendAsync(IPacket packet, CancellationToken ct = default)
    {
        SendPacketCallCount++;
        if (packet is Control ping && _catalog.TryDequeue(out IPacket? response) && response is Control pong)
        {
            var h = pong.Header;
            h.SequenceId = ping.Header.SequenceId;
            pong.Header = h;

            byte[] data = new byte[PacketConstants.HeaderSize];
            ushort opCode = Control.StaticOpCode;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan((int)PacketHeaderOffset.OpCode), opCode);

            using BufferLease lease = BufferLease.CopyFrom(data);
            OnMessageReceived?.Invoke(this, lease);
        }
        return Task.CompletedTask;
    }

    public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default) => SendAsync(packet, ct);
    public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default) => Task.CompletedTask;

    public void EnqueueNextPacket(IPacket packet) => _catalog.Enqueue(packet);

    public override void ResetSequenceCounters() { }

    protected override void Dispose(bool disposing) { }
}
