// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Observability.Contracts;

/// <summary>
/// JSON property names used for each subsystem section inside
/// <see cref="RuntimeObservation.ObservationData"/>.
/// </summary>
/// <remarks>
/// The payload is a single UTF-8 JSON object whose top-level properties are the sections listed here.
/// A requested subsystem that is not registered on the server is omitted rather than written as <c>null</c>.
/// </remarks>
public static class RuntimeObservationSections
{
    /// <summary>Section name for <see cref="RuntimeObservationTargets.PROCESS"/>.</summary>
    public const string Process = "Process";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.DISPATCH"/>.</summary>
    public const string Dispatch = "Dispatch";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.TASKS"/>.</summary>
    public const string Tasks = "Tasks";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.BUFFERS"/>.</summary>
    public const string Buffers = "Buffers";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.CONNECTIONS"/>.</summary>
    public const string Connections = "Connections";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.INSTANCES"/>.</summary>
    public const string Instances = "Instances";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.OBJECT_POOLS"/>.</summary>
    public const string ObjectPools = "ObjectPools";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.CONNECTION_GUARD"/>.</summary>
    public const string ConnectionGuard = "ConnectionGuard";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.CONCURRENCY_GATE"/>.</summary>
    public const string ConcurrencyGate = "ConcurrencyGate";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.POLICY_RATE_LIMITER"/>.</summary>
    public const string PolicyRateLimiter = "PolicyRateLimiter";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.TOKEN_BUCKET_LIMITER"/>.</summary>
    public const string TokenBucketLimiter = "TokenBucketLimiter";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.SESSIONS"/>.</summary>
    public const string Sessions = "Sessions";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.LISTENERS"/>.</summary>
    public const string Listeners = "Listeners";

    /// <summary>Section name for <see cref="RuntimeObservationTargets.PROTOCOLS"/>.</summary>
    public const string Protocols = "Protocols";
}
