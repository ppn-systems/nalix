// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Internal.Security;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.Internal.Connections;

internal sealed class ConnectionBacking : IPoolStateTracked
{
    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;
    private int _poolState;

    public readonly Lock Lock = new();

    // Per-connection local pool for packet arguments to avoid global pool contention.
    // Size 8 matches the default MaxPerConnectionPendingPackets.
    public LocalPool<ConnectionEventArgs> ArgsPool;
    public LocalPool<PooledConnectEventContext> ContextPool;

    public SocketConnection? Socket;
    public SlidingWindow? UdpReplayWindow;
    public SocketTcpTransport? TcpTransport;
    public SocketUdpTransport? UdpTransport;
    public WebSocketTransport? WsTransport;

    public long BytesSent;
    public long BytesReceived;
    public long PacketsDropped;

    public int ErrorCount;
    public long ErrorWindowStartTicks;
    public int DisposeState; // 0=Active, 1=Closing(Event running), 2=Disposed
    public int CloseSignaled;
    public int IsDispatchingClose; // 0=no, 1=yes
    public int PendingProcessCallbacks;

    public IObjectMap<AttributeKey, object>? Attributes;
    public ConcurrentDictionary<ushort, object>? RateLimitCache;

    public EventHandler<IConnectionEventArgs>? ConnectionClosed;
    public EventHandler<IConnectionEventArgs>? MessageProcessed;
    public EventHandler<IConnectionEventArgs>? MessageProcessing;

    public ref int PoolState => ref _poolState;

    public ConnectionBacking()
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Initialize()
    {
        ArgsPool = new LocalPool<ConnectionEventArgs>(s_pool);
        ContextPool = new LocalPool<PooledConnectEventContext>(s_pool);
    }

    /// <summary>
    /// Records one error at <paramref name="nowTicks"/> and returns the count within the current error window.
    /// When <paramref name="windowTicks"/> has elapsed since the window opened, the count restarts at this error.
    /// </summary>
    /// <remarks>
    /// Lock-free and allocation-free. The window is claimed with a single compare-exchange; the winner clears the
    /// count. An error recorded between the claim and the clear can be dropped, which only under-counts by a
    /// concurrent race at a window boundary.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RecordError(long nowTicks, long windowTicks)
    {
        long windowStart = Volatile.Read(ref ErrorWindowStartTicks);
        if (nowTicks - windowStart >= windowTicks &&
            Interlocked.CompareExchange(ref ErrorWindowStartTicks, nowTicks, windowStart) == windowStart)
        {
            _ = Interlocked.Exchange(ref ErrorCount, 0);
        }

        return Interlocked.Increment(ref ErrorCount);
    }

    public void ResetForPool()
    {
        Socket = null;
        TcpTransport = null;
        UdpTransport = null;
        UdpReplayWindow = null;
        WsTransport = null;

        BytesSent = 0;
        BytesReceived = 0;
        PacketsDropped = 0;

        ErrorCount = 0;
        ErrorWindowStartTicks = 0;
        DisposeState = 0;
        CloseSignaled = 0;
        IsDispatchingClose = 0;
        PendingProcessCallbacks = 0;

        Attributes?.Return();
        Attributes = null;
        RateLimitCache?.Clear();

        ConnectionClosed = null;
        MessageProcessing = null;
        MessageProcessed = null;

        ArgsPool.Destroy();
        ContextPool.Destroy();
    }
}
