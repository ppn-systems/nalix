// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Memory;
using Nalix.Framework.Memory.Objects;
using Nalix.SDK.Options;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Tests.Transport.Extensions;

[Collection(TestCollections.RealServer)]
public sealed class ResumeExtensionsPoolingTests
{
    [Fact]
    public async Task ResumeSessionAsync_WhenServerRejects_ReturnsResponsePacketToPool()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        IObjectPoolManager? previous = PacketRegistry.Manager;
        using ObjectPoolManager pools = new();
        PacketRegistry.Configure(pools);

        try
        {
            using ReplyingSession session = new();
            session.State.SessionToken = 42;
            session.State.Secret = new Bytes32(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });

            long returnsBefore = pools.TotalReturnOperations;
            ProtocolReason reason = await session.ResumeSessionAsync();

            Assert.Equal(ProtocolReason.SESSION_EXPIRED, reason);
            Assert.True(
                pools.TotalReturnOperations > returnsBefore,
                "The pooled SessionResume response was never returned to the pool.");
        }
        finally
        {
            PacketRegistry.Configure(previous);
        }
    }

    private sealed class ReplyingSession : TransportSession
    {
        public override TransportOptions Options { get; } = new() { ResumeTimeoutMillis = 5000 };
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

        public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default) => Task.CompletedTask;

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task SendAsync(IPacket packet, CancellationToken ct = default) => this.SendAsync(packet, null, ct);

        public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
        {
            SessionResume reply = new();
            reply.Initialize(SessionResumeStage.RESPONSE, 42, ProtocolReason.SESSION_EXPIRED);
            using BufferLease lease = BufferLease.CopyFrom(reply.Serialize());
            OnMessageReceived?.Invoke(this, lease);
            return Task.CompletedTask;
        }

        public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default) => Task.CompletedTask;

        public override void ResetSequenceCounters() { }

        protected override void Dispose(bool disposing) { }
    }
}
