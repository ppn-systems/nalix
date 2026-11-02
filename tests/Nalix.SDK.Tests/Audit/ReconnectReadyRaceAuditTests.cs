// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security.Hashing;
using Nalix.Environment.Memory;
using Nalix.Environment.Time;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;

namespace Nalix.SDK.Tests.Audit;

// SDK-03 audit reproduction: ReconnectSupervisor checks the disconnect flag and the live connection state,
// then publishes "ready". A drop that lands between that check and the publish is recorded only in the
// flag, which was already read, so the supervisor publishes ready for a session that is no longer connected.
[Trait("Category", "Audit")]
public sealed class ReconnectReadyRaceAuditTests : IDisposable
{
    public ReconnectReadyRaceAuditTests()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task ReconnectLoop_WhenDropLandsAfterResumeBeforeReadyPublish_DoesNotReportReadyForDeadSession()
    {
        using ResumingSession session = new();
        session.State.SessionToken = 42;
        session.State.Secret = Bytes32.Parse(new string('5', 64));

        await session.ConnectAsync();
        Assert.True(session.ConnectedNow);

        // Ready is captured synchronously with the drop, while the supervisor is still inside its backoff.
        session.RaiseDrop();
        Task ready = session.WaitUntilReadyAsync(CancellationToken.None);

        await ready.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(
            session.ConnectedNow,
            "Ready was published for a session that had already dropped: the drop between the flag check and the publish was lost.");
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);

    /// <summary>
    /// Answers session resume with a valid success response, then injects one drop at the first
    /// <c>IsConnected</c> read after the resume hook has run. That read is the supervisor's post-resume
    /// check, so the drop lands exactly between the check and the publish.
    /// </summary>
    private sealed class ResumingSession : TransportSession
    {
        private bool _connected;
        private bool _armed;
        private bool _dropInjected;
        private EventHandler<IBufferLease>? _onMessage;
        private EventHandler<Exception>? _onDisconnected;

        public ResumingSession()
        {
            this.OnResumedAsync = _ =>
            {
                _armed = true;
                return Task.CompletedTask;
            };
        }

        public override TransportOptions Options { get; } = new()
        {
            AutoReconnectEnabled = true,
            ResumeEnabled = true,
            ReconnectMaxAttempts = 0,
            ReconnectBaseDelayMillis = 200,
            ReconnectMaxDelayMillis = 200,
        };

        public bool ConnectedNow => _connected;

        public override bool IsConnected
        {
            get
            {
                bool current = _connected;
                if (_armed && !_dropInjected)
                {
                    _dropInjected = true;
                    _connected = false;
                    _onDisconnected?.Invoke(this, new NetworkException("injected drop"));
                }

                return current;
            }
        }

        public override event EventHandler? OnConnected
        {
            add { }
            remove { }
        }

        public override event EventHandler<Exception>? OnDisconnected
        {
            add => _onDisconnected += value;
            remove => _onDisconnected -= value;
        }

        public override event EventHandler<IBufferLease>? OnMessageReceived
        {
            add => _onMessage += value;
            remove => _onMessage -= value;
        }

        public override event EventHandler<Exception>? OnError
        {
            add { }
            remove { }
        }

        public void RaiseDrop()
        {
            _connected = false;
            _onDisconnected?.Invoke(this, new NetworkException("drop"));
        }

        public override Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
        {
            _connected = true;
            return Task.CompletedTask;
        }

        public override Task DisconnectAsync()
        {
            _connected = false;
            return Task.CompletedTask;
        }

        public override Task SendAsync(IPacket packet, CancellationToken ct = default)
            => this.SendAsync(packet, encrypt: null, ct);

        public override Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
        {
            if (packet is SessionResume { Stage: SessionResumeStage.REQUEST })
            {
                this.RespondToResume();
            }

            return Task.CompletedTask;
        }

        public override Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public override void ResetSequenceCounters() { }

        protected override void Dispose(bool disposing) { }

        private void RespondToResume()
        {
            Span<byte> message = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(message, this.State.SessionToken);
            BinaryPrimitives.WriteInt64LittleEndian(message[8..], Clock.UnixSecondsNow() / 30);

            Span<byte> proof = stackalloc byte[Bytes32.Size];
            HmacKeccak256.Compute(this.State.Secret.AsSpan(), message, proof);

            using SessionResume response = new();
            response.Initialize(SessionResumeStage.RESPONSE, this.State.SessionToken, ProtocolReason.NONE, new Bytes32(proof));

            byte[] data = response.Serialize();
            using BufferLease lease = BufferLease.CopyFrom(data);
            _onMessage?.Invoke(this, lease);
        }
    }
}
