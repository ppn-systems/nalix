// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Tunneling.Tests.TestSupport;

internal sealed class FakeConnection : IConnection
{
    private static long s_nextId = 5000;

    public FakeConnection(string address = "127.0.0.1")
    {
        this.NetworkEndpoint = new FakeEndpoint(address);
        this.ConnectionId = (ulong)Interlocked.Increment(ref s_nextId);
    }

    public FakeTransport FakeTcp { get; } = new();
    public bool Disconnected { get; private set; }
    public string? DisconnectReason { get; private set; }
    public bool IsDisposed { get; private set; }
    public ulong ConnectionId { get; }
    public string? UserId { get; set; }
    public long UpTime => 10;
    public long LastPingTime => 0;
    public bool ExcludeFromIdleTimeout { get; set; }
    public IOpCodeExtractor PacketClassifier { get; set; } = null!;
    public INetworkEndpoint NetworkEndpoint { get; }
    public IObjectMap<AttributeKey, object> Attributes { get; } = ObjectMap<AttributeKey, object>.Rent();
    public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
    public Bytes32 Secret { get; set; }
    public PermissionLevel Level { get; set; }
    public CipherSuiteType Algorithm { get; set; }
    public IConnection.ITransport Transport => this.FakeTcp;

#pragma warning disable CS0067
    public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
    public event EventHandler<IConnectionEventArgs>? MessageProcessing;
    public event EventHandler<IConnectionEventArgs>? MessageProcessed;
#pragma warning restore CS0067

    /// <summary>Raises <see cref="ConnectionClosed"/> as the transport would when the socket closes.</summary>
    public void RaiseClosed() => this.ConnectionClosed?.Invoke(this, new FakeEventArgs(this));

    public void Disconnect(string? reason = null)
    {
        this.Disconnected = true;
        this.DisconnectReason = reason;
    }

    public void Dispose()
    {
        if (!this.IsDisposed)
        {
            this.IsDisposed = true;
            this.Secret = Bytes32.Zero;
            this.Attributes.Return();
        }
    }

    public int ErrorCount { get; private set; }
    public void IncrementErrorCount() => this.ErrorCount++;
    public int IdleTimeoutMs { get; set; } = 60_000;
    public void UpdateIdleTimeout(int newTimeoutMs) => this.IdleTimeoutMs = newTimeoutMs;

    private sealed class FakeEventArgs(IConnection connection) : IConnectionEventArgs
    {
        public IConnection Connection { get; } = connection;
        public IBufferLease? Lease => null;
        public INetworkEndpoint? NetworkEndpoint => this.Connection.NetworkEndpoint;
        public void Dispose() { }
    }
}
