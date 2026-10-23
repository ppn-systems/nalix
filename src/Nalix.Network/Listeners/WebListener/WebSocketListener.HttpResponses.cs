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
        "HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\nConnection: close\r\n\r\n"u8;

    private static ReadOnlySpan<byte> ForbiddenOriginResponse =>
        "HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain\r\nContent-Length: 16\r\nConnection: close\r\n\r\nOrigin forbidden"u8;

    private static ReadOnlySpan<byte> BadRequestResponse =>
        "HTTP/1.1 400 Bad Request\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/plain\r\nContent-Length: 11\r\nConnection: close\r\n\r\nBad Request"u8;

    private static ReadOnlySpan<byte> NotFoundResponse =>
        "HTTP/1.1 404 Not Found\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/plain\r\nContent-Length: 9\r\nConnection: close\r\n\r\nNot Found"u8;

    private static ReadOnlySpan<byte> MethodNotAllowedResponse =>
        "HTTP/1.1 405 Method Not Allowed\r\nAccess-Control-Allow-Origin: *\r\nAllow: GET, POST, OPTIONS\r\nContent-Type: text/plain\r\nContent-Length: 18\r\nConnection: close\r\n\r\nMethod Not Allowed"u8;

    private static ReadOnlySpan<byte> PayloadTooLargeResponse =>
        "HTTP/1.1 413 Payload Too Large\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/plain\r\nContent-Length: 17\r\nConnection: close\r\n\r\nPayload Too Large"u8;

    private static ReadOnlySpan<byte> InternalServerErrorResponse =>
        "HTTP/1.1 500 Internal Server Error\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: text/plain\r\nContent-Length: 21\r\nConnection: close\r\n\r\nInternal Server Error"u8;
}
