// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;

namespace Nalix.SDK.Traversal.Tests.TestSupport;

/// <summary>
/// A control session that never touches the network. Tests observe the packets the client sends through
/// <see cref="OnSend"/> and inject inbound packets with <see cref="Deliver"/>, synchronously and in order.
/// </summary>
internal sealed class FakeTraversalSession : TransportSession
{
    public override TransportOptions Options { get; } = new();
    public override bool IsConnected => true;

    /// <summary>Invoked for every packet sent, after the client has subscribed to its replies.</summary>
    public Action<IPacket>? OnSend { get; set; }

    public override event EventHandler? OnConnected
    {
        add { }
        remove { }
    }

    public override event EventHandler<Exception>? OnDisconnected
    {
        add { }
        remove { }
    }

    public override event EventHandler<IBufferLease>? OnMessageReceived;

    public override event EventHandler<Exception>? OnError
    {
        add { }
        remove { }
    }

    public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task DisconnectAsync() => Task.CompletedTask;

    public override Task SendAsync(IPacket packet, CancellationToken ct = default)
    {
        this.OnSend?.Invoke(packet);
        return Task.CompletedTask;
    }

    public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
    {
        this.OnSend?.Invoke(packet);
        return Task.CompletedTask;
    }

    public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ResetSequenceCounters() { }

    protected override void Dispose(bool disposing) { }

    /// <summary>Serializes <paramref name="packet"/> and raises it as if it had arrived from the server.</summary>
    public void Deliver(IPacket packet)
    {
        byte[] data = packet.Serialize();
        using BufferLease lease = BufferLease.CopyFrom(data);
        this.OnMessageReceived?.Invoke(this, lease);
    }
}
