// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Network.Listeners.Web;

public abstract partial class WebSocketListenerBase
{
    private static ReadOnlySpan<byte> HandshakeResponseSuffix => "\r\n\r\n"u8;

    private static ReadOnlySpan<byte> HandshakeSubProtocolPrefix => "\r\nSec-WebSocket-Protocol: "u8;

    private static ReadOnlySpan<byte> HandshakeResponsePrefix
        => "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "u8;

    private static ReadOnlySpan<byte> HealthzResponse =>
        "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nHealthy"u8;

    private static ReadOnlySpan<byte> CorsPreflightResponse =>
        "HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\nConnection: close\r\n\r\n"u8;

    private static ReadOnlySpan<byte> ForbiddenOriginResponse =>
        "HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain\r\nContent-Length: 16\r\nConnection: close\r\n\r\nOrigin forbidden"u8;

}
