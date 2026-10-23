// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;

namespace Nalix.SDK.Tests.TestSupport;

/// <summary>A session that never touches the network and lets a test raise message and disconnect events on demand.</summary>
internal sealed class RaisableFakeSession : TransportSession
{
    private IPacket _next = new Control();

    public override TransportOptions Options { get; } = new();
    public override bool IsConnected => true;

    public override event EventHandler? OnConnected
    {
        add { }
        remove { }
    }
    public override event EventHandler<Exception>? OnDisconnected;
    public override event EventHandler<IBufferLease>? OnMessageReceived;
    public override event EventHandler<Exception>? OnError
    {
        add { }
        remove { }
    }

    public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task DisconnectAsync()
    {
        OnDisconnected?.Invoke(this, new InvalidOperationException("disconnect"));
        return Task.CompletedTask;
    }

    public override Task SendAsync(IPacket packet, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ResetSequenceCounters() { }

    protected override void Dispose(bool disposing)
    {
    }

    public void SetNextPacket(IPacket packet) => _next = packet;

    public void EnsureRegistry() => PacketRegistry.Build();

    public void RaiseMessage()
    {
        byte[] data = _next.Serialize();

        using BufferLease lease = BufferLease.CopyFrom(data);
        OnMessageReceived?.Invoke(this, lease);
    }

    public void RaiseDisconnect(Exception ex) => OnDisconnected?.Invoke(this, ex);
}
