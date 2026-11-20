// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Environment.Datagrams;

/// <summary>
/// Lifecycle of a <see cref="DatagramEngine"/>.
/// </summary>
public enum DatagramEngineState : byte
{
    /// <summary>Created; no datagram has authenticated yet.</summary>
    Pending = 0,

    /// <summary>A datagram from the peer authenticated; data flows.</summary>
    Established = 1,

    /// <summary>A CLOSE frame is queued or was sent or received; the session ignores further datagrams.</summary>
    Draining = 2,

    /// <summary>Closed; nothing is sent or received.</summary>
    Closed = 3,
}
