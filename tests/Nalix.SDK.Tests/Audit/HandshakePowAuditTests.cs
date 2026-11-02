// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Tests.Audit;

// SDK-02 audit reproduction: the proof-of-work solver runs on a server-chosen difficulty, is never bounded
// by ConnectTimeoutMillis, and ignores cancellation. A hostile or misconfigured server can therefore pin a
// client thread indefinitely during the handshake.
[Trait("Category", "Audit")]
public sealed class HandshakePowAuditTests
{
    [Fact]
    public async Task HandshakeAsync_WhenServerDemandsHighDifficultyProofOfWork_HonorsConnectTimeout()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        using PowDemandingSession session = new();
        session.Options.ServerPublicKey = new string('0', 64);
        session.Options.ConnectTimeoutMillis = 500;

        // Difficulty 40 needs roughly 2^40 Keccak evaluations: effectively unbounded for the solver.
        // Run on the thread pool: the fake delivers the challenge synchronously, so without this the
        // solver would spin on the test thread itself and the bounded wait below could never run.
        Task handshake = Task.Run(() => session.HandshakeAsync().AsTask());

        Task winner = await Task.WhenAny(handshake, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(
            ReferenceEquals(handshake, winner),
            "HandshakeAsync did not finish within ConnectTimeoutMillis (500 ms): the proof-of-work solver ignored the timeout.");
    }

    /// <summary>
    /// Replies to the client's <see cref="SessionInit"/> with POW_REQUIRED and, when asked for the PoW
    /// challenge, returns a challenge with difficulty 40. Replies echo the request's SequenceId.
    /// </summary>
    private sealed class PowDemandingSession : TransportSession
    {
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

        private EventHandler<IBufferLease>? _onMessageReceived;

        public override event EventHandler<IBufferLease>? OnMessageReceived
        {
            add => _onMessageReceived += value;
            remove => _onMessageReceived -= value;
        }

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
            IPacket? reply = packet switch
            {
                SessionInit => BuildPowRequired(),
                Control { Type: ControlType.POW_REQUEST } => BuildChallenge(),
                _ => null,
            };

            if (reply is not null)
            {
                PacketHeader header = reply.Header;
                header.SequenceId = packet.Header.SequenceId;
                reply.Header = header;

                byte[] data = reply.Serialize();
                using BufferLease lease = BufferLease.CopyFrom(data);
                _onMessageReceived?.Invoke(this, lease);
            }

            return Task.CompletedTask;
        }

        public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override void ResetSequenceCounters() { }

        protected override void Dispose(bool disposing) { }

        private static Control BuildPowRequired()
        {
            Control control = new();
            control.Initialize(ControlType.ERROR, flags: PacketFlags.SYSTEM, reasonCode: ProtocolReason.POW_REQUIRED);
            return control;
        }

        private static ProofOfWorkChallenge BuildChallenge()
        {
            ProofOfWorkChallenge challenge = new();
            challenge.Initialize(
                Bytes32.Parse(new string('1', 64)),
                40,
                DateTime.UtcNow.Ticks,
                Bytes32.Parse(new string('2', 64)),
                PacketFlags.SYSTEM);
            return challenge;
        }
    }
}
