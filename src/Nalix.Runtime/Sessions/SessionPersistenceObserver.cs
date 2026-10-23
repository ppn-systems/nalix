// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Extensions;

namespace Nalix.Runtime.Sessions;

/// <summary>
/// Observes connection unregistrations and automatically persists session data if applicable.
/// Decouples the session service from the connection hub.
/// </summary>
public sealed class SessionPersistenceObserver : IDisposable
{
    private bool _disposed;

    private readonly IConnectionHub _hub;
    private readonly ISessionService _sessionService;

    /// <summary>
    /// Initializes a new instance of the SessionPersistenceObserver and subscribes to hub events.
    /// </summary>
    public SessionPersistenceObserver(IConnectionHub hub, ISessionService sessionService)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));

        _hub.ConnectionUnregistered += this.OnConnectionClosed;
    }

    private void OnConnectionClosed(IConnection connection)
    {
        if (connection is null || connection.IsDisposed)
        {
            return;
        }

        SnapshotConnection snapshot = new(connection);
        _ = PersistBackgroundAsync(_sessionService, snapshot);
    }

    private static async Task PersistBackgroundAsync(ISessionService service, SnapshotConnection snapshot)
    {
        try
        {
            await service.SaveSessionAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // Background persistence failures are ignored in fire-and-forget scenarios
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _hub.ConnectionUnregistered -= this.OnConnectionClosed;
    }

    private sealed class SnapshotSequenceCounter(uint value) : ISequenceCounter
    {
        private uint _value = value;
        public uint Next() => ++_value;
        public uint Current() => _value;
        public void Reset(uint newValue = 0) => _value = newValue;
        public bool IsValid(uint? receivedSeq, uint window = 0) => true;
        public void UpdateTo(uint receivedSeq) => _value = receivedSeq;
        public void ResumeFrom(uint lastKnownSeq, uint safetyGap = 1000) => _value = lastKnownSeq + safetyGap;
        public bool IsApproachingOverflow(uint margin = 1_000_000) => false;
    }

    private sealed class SnapshotTransport(uint sendSeq, uint receiveSeq, TransportFraming framing) : IConnection.ITransport
    {
        public TransportFraming Framing { get; private set; } = framing;
        public ISequenceCounter SendSequence { get; } = new SnapshotSequenceCounter(sendSeq);
        public ISequenceCounter ReceiveSequence { get; } = new SnapshotSequenceCounter(receiveSeq);
        public uint NextSendSequence() => this.SendSequence.Next();
        public uint NextReceiveSequence() => this.ReceiveSequence.Next();
        public uint CurrentSendSequence => this.SendSequence.Current();
        public uint CurrentReceiveSequence => this.ReceiveSequence.Current();
        public void Send(ReadOnlySpan<byte> message) { }
        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void BeginReceive(CancellationToken cancellationToken = default) { }
        public void UseFraming(TransportFraming newFraming) => this.Framing = newFraming;
    }

    private sealed class SnapshotConnection : IConnection
    {
        private readonly ObjectMap<AttributeKey, object> _attributes;
        private Bytes32 _secret;

        public SnapshotConnection(IConnection source)
        {
            this.ConnectionId = source.ConnectionId;
            this.UserId = source.UserId;
            this.UpTime = source.UpTime;
            this.LastPingTime = source.LastPingTime;
            this.ExcludeFromIdleTimeout = source.ExcludeFromIdleTimeout;
            this.PacketClassifier = source.PacketClassifier;
            this.NetworkEndpoint = source.NetworkEndpoint;
            this.Level = source.Level;
            this.Algorithm = source.Algorithm;
            this.ErrorCount = source.ErrorCount;
            this.IsUdpCreated = source.IsUdpCreated;
            _secret = source.Secret;

            uint tcpSend = source.TCP is not null ? source.TCP.SendSequence.Current() : 0;
            uint tcpRecv = source.TCP is not null ? source.TCP.ReceiveSequence.Current() : 0;
            TransportFraming tcpFraming = source.TCP is not null ? source.TCP.Framing : TransportFraming.UInt16LengthPrefixed;
            this.TCP = new SnapshotTransport(tcpSend, tcpRecv, tcpFraming);

            if (source.IsUdpCreated && source.UDP is not null)
            {
                this.UDP = new SnapshotTransport(
                    source.UDP.SendSequence.Current(),
                    source.UDP.ReceiveSequence.Current(),
                    source.UDP.Framing);
            }

            _attributes = ObjectMap<AttributeKey, object>.Rent();
            if (source.Attributes is not null)
            {
                foreach (KeyValuePair<AttributeKey, object> kvp in source.Attributes)
                {
                    if (kvp.Key == ConnectionAttributes.RuntimeState)
                    {
                        continue;
                    }

                    _attributes[kvp.Key] = kvp.Value;
                }
            }

            bool handshakeEstablished = source.GetRuntimeState().HandshakeEstablished;
            this.GetRuntimeState().HandshakeEstablished = handshakeEstablished;
        }

        public bool IsDisposed { get; private set; }
        public bool IsUdpCreated { get; }
        public ulong ConnectionId { get; }
        public string? UserId { get; set; }
        public long UpTime { get; }
        public long LastPingTime { get; }
        public bool ExcludeFromIdleTimeout { get; set; }
        public IOpCodeExtractor PacketClassifier { get; }
        public INetworkEndpoint NetworkEndpoint { get; }
        public IObjectMap<AttributeKey, object> Attributes => _attributes;
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public Bytes32 Secret
        {
            get => _secret;
            set => _secret = value;
        }
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }
        public IConnection.ITransport TCP { get; }
        public IConnection.ITransport? UDP { get; }

#pragma warning disable CS0067
        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;
#pragma warning restore CS0067

        public void Disconnect(string? reason = null) { }

        public void Dispose()
        {
            if (this.IsDisposed)
            {
                return;
            }

            this.IsDisposed = true;
            _secret = Bytes32.Zero;
            _attributes.Return();
        }

        public int ErrorCount { get; private set; }
        public void IncrementErrorCount() => this.ErrorCount++;
        public int IdleTimeoutMs { get; private set; }
        public void UpdateIdleTimeout(int newTimeoutMs) => this.IdleTimeoutMs = newTimeoutMs;
    }
}
