// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Abstractions.Networking;

/// <summary>
/// Defines the available network transport protocols supported by the Nalix framework.
/// </summary>
public enum NetworkTransport : byte
{
    /// <summary>
    /// Transmission Control Protocol (Reliable).
    /// </summary>
    TCP,

    /// <summary>
    /// User Datagram Protocol (Unreliable).
    /// </summary>
    UDP,

    /// <summary>
    /// WebSocket Protocol (Reliable, message-oriented).
    /// </summary>
    WEBSOCKET,

    /// <summary>
    /// Nalix Datagram Protocol: an authenticated UDP session with reliable, sequenced and unreliable
    /// delivery on independent channels.
    /// </summary>
    DATAGRAM,
}
