// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Network.Options;

/// <summary>
/// Represents configuration settings for idle connection timeout in the network layer.
/// Defines how long an inactive connection can stay open before being automatically closed.
/// </summary>
[IniComment("Timing wheel configuration — controls idle timeout detection for TCP and WebSocket connections")]
public sealed partial class TimingWheelOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>
    /// Gets or sets the size of the timing wheel (number of buckets).
    /// Higher values reduce collisions but use a bit more memory.
    /// </summary>
    [IniComment("Number of timing wheel buckets — higher values reduce slot collisions (minimum 1)")]
    [ValueRange(1, int.MaxValue)]
    public int BucketCount { get; set; } = 512;

    /// <summary>
    /// Gets or sets the precision of the idle check tick in milliseconds.
    /// Lower values mean more frequent checks but slightly higher CPU usage.
    /// </summary>
    [IniComment("Tick interval in milliseconds — lower = more precise but higher CPU usage (minimum 1)")]
    [ValueRange(1, int.MaxValue)]
    public int TickDuration { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the idle timeout for TCP connections in milliseconds.
    /// This closes live connections that stop sending traffic; it does not control how long
    /// disconnected sessions remain resumable. Use <c>SessionStoreOptions.SessionTtl</c> for that.
    /// </summary>
    [IniComment("TCP connection idle timeout in milliseconds before auto-close (default 60000 = 60s)")]
    [ValueRange(1, int.MaxValue)]
    public int IdleTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// Gets or sets the maximum time in milliseconds a TCP or WebSocket connection may stay open
    /// while its permission level is still below <c>PermissionLevel.ESTABLISHED</c>, measured from
    /// the moment the connection was accepted. Frames received before authentication do not extend
    /// this deadline. Connections that reach <c>ESTABLISHED</c> (handshake or session resume) are not
    /// affected. Set to 0 to disable the deadline.
    /// </summary>
    [IniComment("Maximum time in milliseconds a TCP/WebSocket connection may stay below ESTABLISHED permission after accept, before auto-close; frames do not extend it (default 15000 = 15s, 0 = disabled)")]
    [ValueRange(0, 600_000)]
    public int HandshakeTimeoutMs { get; set; } = 15_000;

    /// <summary>
    /// Gets or sets the maximum time in milliseconds to wait for the timing wheel to drain
    /// gracefully during shutdown before forceful termination.
    /// </summary>
    [IniComment("Maximum time in milliseconds to wait for the timing wheel to drain gracefully during shutdown (default: 5000)")]
    [ValueRange(0, 60000)]
    public int WheelDrainTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Validates the configuration options and throws an exception if validation fails.
    /// </summary>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// Thrown when one or more values are outside their allowed range.
    /// </exception>
    public void Validate() => this.ValidateDataAnnotations();
}
