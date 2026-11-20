// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Environment.Datagrams;

/// <summary>
/// Why a datagram session closed. Carried in the CLOSE frame, except <see cref="IdleTimeout"/>, which is local only.
/// </summary>
public enum DatagramCloseCode : byte
{
    /// <summary>Normal close.</summary>
    NoError = 0,

    /// <summary>The peer hit an internal error.</summary>
    InternalError = 1,

    /// <summary>The peer violated the protocol.</summary>
    ProtocolViolation = 2,

    /// <summary>A frame could not be decoded, or its type is unknown.</summary>
    FrameEncodingError = 3,

    /// <summary>The peer exceeded an advertised receive budget.</summary>
    FlowControlError = 4,

    /// <summary>Nothing was received within the idle timeout. Never sent on the wire.</summary>
    IdleTimeout = 5,

    /// <summary>Too many datagrams failed to decrypt.</summary>
    AeadLimitReached = 6,

    /// <summary>The application closed the session with its own code.</summary>
    ApplicationError = 7,
}
