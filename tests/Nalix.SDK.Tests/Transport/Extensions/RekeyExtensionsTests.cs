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

    [Fact]
    public async Task RekeyAsync_OnOrderedTransportWhenAckTimesOut_KeepsTheNewSecret()
    {
        // On TCP/WebSocket the server switches as soon as it reads SESSION_REKEY, so a late acknowledgement must not
        // send the client back to the old key (that is what left the two sides on different keys).
        Bytes32 previousSecret = Secret(0x42);
        TimeoutSession session = new(ordered: true);
        session.State.Secret = previousSecret;
        session.Options.ConnectTimeoutMillis = 50;

        _ = await Assert.ThrowsAsync<TimeoutException>(async () => await session.RekeyAsync());

        Assert.NotEqual(previousSecret, session.State.Secret);
        Assert.Equal(1, session.ResetCount);
    }

    [Fact]
    public async Task RekeyAsync_WhenTheRequestCannotBeSent_KeepsTheSecretAndSequenceCounters()
    {
        // Nothing reached the server, so neither the key nor the replay windows may change; resetting the counters
        // here used to desynchronise them from the server's.
        Bytes32 previousSecret = Secret(0x42);
        TimeoutSession session = new(ordered: true, failSend: true);
        session.State.Secret = previousSecret;
        session.Options.ConnectTimeoutMillis = 50;

        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await session.RekeyAsync());

        Assert.Equal(previousSecret, session.State.Secret);
        Assert.Equal(0, session.ResetCount);
    }

    [Fact]
    public async Task RekeyAsync_OnASessionThatIsNotATransportSession_SendsThenSwitchesAndRestoresOnTimeout()
    {
        Bytes32 previousSecret = Secret(0x24);
        InterfaceOnlySession session = new();
        session.State.Secret = previousSecret;
        session.Options.ConnectTimeoutMillis = 50;

        _ = await Assert.ThrowsAsync<TimeoutException>(async () => await session.RekeyAsync());

        Assert.Equal(1, session.Sent);
        Assert.Equal(previousSecret, session.State.Secret);
    }

    private static Bytes32 Secret(byte fill)
    {
        byte[] secretBytes = new byte[Bytes32.Size];
        Array.Fill(secretBytes, fill);
        return new Bytes32(secretBytes);
    }

    /// <summary>An <see cref="ITransportSession"/> that is not a <see cref="TransportSession"/> and never replies.</summary>
    private sealed class InterfaceOnlySession : ITransportSession
    {
        public int Sent { get; private set; }

        public TransportOptions Options { get; } = new();

        public SessionState State { get; } = new();

        public bool IsConnected => true;

        public Func<CancellationToken, Task>? OnReauthenticateAsync { get; set; }

        public Func<CancellationToken, Task>? OnResumedAsync { get; set; }

#pragma warning disable CS0067
        public event EventHandler? OnConnected;
        public event EventHandler? OnReady;
        public event EventHandler<Exception>? OnDisconnected;
        public event EventHandler<IBufferLease>? OnMessageReceived;
        public event EventHandler<Exception>? OnError;
#pragma warning restore CS0067

        public Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task SendAsync(IPacket packet, CancellationToken ct = default)
        {
            this.Sent++;
            return Task.CompletedTask;
        }

        public Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default) => this.SendAsync(packet, ct);

        public Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default) => Task.CompletedTask;

        public void ResetSequenceCounters() { }

        public Task WaitUntilReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TimeoutSession(bool ordered = false, bool failSend = false) : TransportSession
    {
        public int ResetCount { get; private set; }

        internal override bool DeliversRekeyReliably => ordered;

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
            => failSend ? Task.FromException(new InvalidOperationException("socket closed")) : Task.CompletedTask;

        public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override void ResetSequenceCounters() => this.ResetCount++;

        protected override void Dispose(bool disposing) { }
    }
}
