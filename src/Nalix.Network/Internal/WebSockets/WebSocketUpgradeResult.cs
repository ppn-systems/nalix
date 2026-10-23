// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Network.Internal.WebSockets;

/// <summary>
/// Represents the result of parsing a WebSocket HTTP upgrade request.
/// </summary>
internal readonly ref struct WebSocketUpgradeResult
{
    public bool IsValid { get; init; }
    public bool IsHeaderComplete { get; init; }
    public bool IsMalformed { get; init; }
    public int BytesConsumed { get; init; }
    public int ContentLength { get; init; }

    public ReadOnlySpan<byte> Path { get; init; }
    public ReadOnlySpan<byte> Query { get; init; }
    public ReadOnlySpan<byte> Origin { get; init; }
    public ReadOnlySpan<byte> SubProtocol { get; init; }
    public ReadOnlySpan<byte> HttpMethod { get; init; }
    public ReadOnlySpan<byte> ContentType { get; init; }
    public ReadOnlySpan<byte> SecWebSocketKey { get; init; }
    public ReadOnlySpan<byte> CfConnectingIp { get; init; }
    public ReadOnlySpan<byte> XRealIp { get; init; }
    public ReadOnlySpan<byte> XForwardedFor { get; init; }
}
