// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Tests.Transport.Extensions;

public sealed class RekeyExtensionsTests
{
    [Fact]
    public async Task RekeyAsync_WhenAckTimesOut_RestoresPreviousSecret()
    {
        byte[] secretBytes = new byte[Bytes32.Size];
        Array.Fill<byte>(secretBytes, 0x42);
        Bytes32 previousSecret = new(secretBytes);

        TimeoutSession session = new();
        session.State.Secret = previousSecret;
        session.Options.ConnectTimeoutMillis = 50;

        _ = await Assert.ThrowsAsync<TimeoutException>(async () => await session.RekeyAsync());

        Assert.Equal(previousSecret, session.State.Secret);
    }

    private sealed class TimeoutSession : TransportSession
    {
        public override TransportOptions Options { get; } = new();
        public override bool IsConnected => true;

#pragma warning disable CS0067
        public override event EventHandler? OnConnected;
        public override event EventHandler<Exception>? OnDisconnected;
        public override event EventHandler<IBufferLease>? OnMessageReceived;
        public override event EventHandler<Exception>? OnError;
#pragma warning restore CS0067

        public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task SendAsync(IPacket packet, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override void ResetSequenceCounters() { }

        protected override void Dispose(bool disposing) { }
    }
}
