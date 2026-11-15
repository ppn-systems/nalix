// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Configuration;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Connections;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Internal.Protocol;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

namespace Nalix.Network.Internal.Initialization;

/// <summary>
/// Ensures that object pools used by the networking layer are configured and preallocated
/// exactly once when the assembly is loaded, regardless of which connection type is instantiated first.
/// </summary>
internal static class NetworkPoolInitializer
{
    static NetworkPoolInitializer()
    {
        ObjectPoolManager pool = ObjectPoolManager.Shared;

        ConnectionGuardOptions s_options = ConfigurationManager.Instance.Get<ConnectionGuardOptions>();
        NetworkSocketOptions socketOptions = ConfigurationManager.Instance.Get<NetworkSocketOptions>();

        // Pre-configure pool capacities based on expected usage patterns to minimize resizing during runtime.
        _ = pool.SetMaxCapacity<SocketConnection>(s_options.MaxConnections);
        _ = pool.SetMaxCapacity<ConnectionBacking>(s_options.MaxConnections);
        _ = pool.SetMaxCapacity<SocketTcpTransport>(s_options.MaxConnections);
        _ = pool.SetMaxCapacity<WebSocketTransport>(s_options.MaxConnections); // Added missing WebSocketTransport
        _ = pool.SetMaxCapacity<ProxyHeaderContext>(s_options.MaxConnections);
        _ = pool.SetMaxCapacity<TimingWheel.TimeoutTask>(s_options.MaxConnections);
        _ = pool.SetMaxCapacity<PooledSocketReceiveContext>(s_options.MaxConnections);

        int capacity = (s_options.MaxConnections * 2) + 1024;

        // Event args and contexts are used per-packet, so we provision extra capacity to handle spikes without immediate contention.
        _ = pool.SetMaxCapacity<ConnectionEventArgs>(capacity);
        _ = pool.SetMaxCapacity<PooledConnectEventContext>(capacity);

        // Configure object pools for accept contexts and socket async event args based on the provided options.
        _ = pool.SetMaxCapacity<PooledAcceptContext>((socketOptions.MaxParallel * 2) + 32);
        _ = pool.Prealloc<PooledAcceptContext>(socketOptions.MaxParallel);

        // Preallocate objects in the pools to improve performance and reduce latency during runtime.
        _ = pool.SetMaxCapacity<PooledSocketAsyncEventArgs>(socketOptions.MaxParallel + s_options.MaxConnections);
        _ = pool.Prealloc<PooledSocketAsyncEventArgs>(socketOptions.MaxParallel * 4);

        _ = pool.Prealloc<TimingWheel.TimeoutTask>(128);

        _ = pool.Prealloc<ConnectionBacking>(128);
        _ = pool.Prealloc<ConnectionEventArgs>(256);
        _ = pool.Prealloc<PooledConnectEventContext>(256);

    }

    public static void InitializeTcp()
    {
        ObjectPoolManager pool = ObjectPoolManager.Shared;

        _ = pool.Prealloc<SocketConnection>(128);

        _ = pool.Prealloc<SocketTcpTransport>(128);
        _ = pool.Prealloc<ProxyHeaderContext>(128);
        _ = pool.Prealloc<PooledSocketReceiveContext>(128);
    }

    public static void InitializeWebSocket()
    {
        ObjectPoolManager pool = ObjectPoolManager.Shared;

        _ = pool.Prealloc<WebSocketTransport>(128);
    }
}
