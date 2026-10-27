// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Framework.Memory.Objects;
using Nalix.Abstractions.Networking.Protocols;

namespace Nalix.Runtime.Tests.TestSupport;

internal sealed class FakeConnection : IConnection
{
    private static ulong s_nextId = 1000;

    public FakeTransport FakeTcp { get; } = new();

    public bool IsDisposed { get; private set; }

    public bool Disconnected { get; private set; }

    public string? DisconnectReason { get; private set; }

    public bool IsUdpCreated => false;

    public ulong ConnectionId { get; } = Interlocked.Increment(ref s_nextId);

    public string? UserId { get; set; }

    public long UpTime => 10;

    public long LastPingTime => 0;

    public bool ExcludeFromIdleTimeout { get; set; }

    public IOpCodeExtractor PacketClassifier { get; set; } = new SimpleOpCodeExtractor();

    public INetworkEndpoint NetworkEndpoint { get; } = new FakeEndpoint();

    public IObjectMap<AttributeKey, object> Attributes { get; } = ObjectMap<AttributeKey, object>.Rent();

    public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();

    public Bytes32 Secret { get; set; }

    public PermissionLevel Level { get; set; }

    public CipherSuiteType Algorithm { get; set; }

    public IConnection.ITransport TCP => FakeTcp;

    public IConnection.ITransport? UDP => null;

#pragma warning disable CS0067
    public event EventHandler<IConnectionEventArgs>? ConnectionClosed;

    public event EventHandler<IConnectionEventArgs>? MessageProcessing;

    public event EventHandler<IConnectionEventArgs>? MessageProcessed;
#pragma warning restore CS0067

    public void Disconnect(string? reason = null)
    {
        Disconnected = true;
        DisconnectReason = reason;
    }

    public void Dispose()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            Secret = Bytes32.Zero;
            Attributes.Return();
        }
    }

    public int ErrorCount { get; private set; }

    public void IncrementErrorCount() => ErrorCount++;

    public int IdleTimeoutMs { get; set; } = 60_000;

    public void UpdateIdleTimeout(int newTimeoutMs) => IdleTimeoutMs = newTimeoutMs;
}
