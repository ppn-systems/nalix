// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Tests.TestSupport;

internal sealed class FakeStreamSession : TransportSession
{
    private readonly Func<int, NullableStreamItem[]> _responseFactory;
    private int _sendCount;

    public FakeStreamSession(params NullableStreamItem[] responses) : this(_ => responses)
    {
    }

    public FakeStreamSession(Func<int, NullableStreamItem[]> responseFactory)
        => _responseFactory = responseFactory;

    public override TransportOptions Options { get; } = new();
    public override bool IsConnected => true;

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
        => this.SendAsync(packet, encrypt: null, ct);

    public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
    {
        int attempt = Interlocked.Increment(ref _sendCount);

        foreach (NullableStreamItem response in _responseFactory(attempt))
        {
            Emit(response);
        }

        return Task.CompletedTask;
    }

    public void Emit(NullableStreamItem response)
    {
        byte[] data = response.Serialize();
        using BufferLease lease = BufferLease.CopyFrom(data);
        this.OnMessageReceived?.Invoke(this, lease);
    }

    public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ResetSequenceCounters() { }

    protected override void Dispose(bool disposing) { }
}
