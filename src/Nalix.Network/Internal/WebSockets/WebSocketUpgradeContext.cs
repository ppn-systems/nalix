// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;

using Nalix.Abstractions;

namespace Nalix.Network.Internal.WebSockets;

/// <summary>
/// State context for a pending WebSocket HTTP upgrade handshake.
/// Intrusive linked list node for fast timeout sweeping.
/// </summary>
internal sealed class WebSocketUpgradeContext : IPoolable
{
    public Socket? Socket;
    public byte[] Buffer = [];
    public int BytesReceived;
    public bool RemovedFromList;
    public int LimiterSlotReleased;
    public long HandshakeStartTimeTicks;
    public EndPoint? RealEndPoint;

    // Intrusive linked list pointers for timeout sweep
    public WebSocketUpgradeContext? Next;
    public WebSocketUpgradeContext? Prev;

    public void ResetForPool()
    {
        Socket = null;
        RealEndPoint = null;
        Buffer = [];
        BytesReceived = 0;
        HandshakeStartTimeTicks = 0;
        RemovedFromList = false;
        LimiterSlotReleased = 0;
        Next = null;
        Prev = null;
    }
}
