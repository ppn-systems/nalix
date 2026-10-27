// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

#pragma warning disable CA1711

namespace Nalix.Observability.Contracts;

/// <summary>
/// Identifies the runtime subsystems to observe. Values combine so a single request can sample
/// every subsystem a dashboard view needs in one round-trip.
/// </summary>
[System.Flags]
public enum RuntimeObservationTargets : uint
{
    /// <summary>
    /// No subsystem selected.
    /// </summary>
    NONE = 0,

    /// <summary>
    /// Process-level counters: CPU time, memory, GC and thread pool.
    /// </summary>
    PROCESS = 1u << 0,

    /// <summary>
    /// Packet dispatch metrics and queue state.
    /// </summary>
    DISPATCH = 1u << 1,

    /// <summary>
    /// Runtime task scheduling and execution metrics.
    /// </summary>
    TASKS = 1u << 2,

    /// <summary>
    /// Buffer pool usage and allocation metrics.
    /// </summary>
    BUFFERS = 1u << 3,

    /// <summary>
    /// Active connection metrics.
    /// </summary>
    CONNECTIONS = 1u << 4,

    /// <summary>
    /// Registered dependency and runtime instance metrics.
    /// </summary>
    INSTANCES = 1u << 5,

    /// <summary>
    /// Object pool usage and lifecycle metrics.
    /// </summary>
    OBJECT_POOLS = 1u << 6,

    /// <summary>
    /// Connection guard throttling and protection metrics.
    /// </summary>
    CONNECTION_GUARD = 1u << 7,

    /// <summary>
    /// Concurrency gate limits and usage metrics.
    /// </summary>
    CONCURRENCY_GATE = 1u << 8,

    /// <summary>
    /// Policy rate limiter counters and state.
    /// </summary>
    POLICY_RATE_LIMITER = 1u << 9,

    /// <summary>
    /// Token bucket limiter counters and state.
    /// </summary>
    TOKEN_BUCKET_LIMITER = 1u << 10,

    /// <summary>
    /// Active session metrics.
    /// </summary>
    SESSIONS = 1u << 11,

    /// <summary>
    /// Listener transport metrics.
    /// </summary>
    LISTENERS = 1u << 12,

    /// <summary>
    /// Protocol transport metrics.
    /// </summary>
    PROTOCOLS = 1u << 13,

    /// <summary>
    /// Every subsystem known to this contract version.
    /// </summary>
    ALL = (1u << 14) - 1
}
