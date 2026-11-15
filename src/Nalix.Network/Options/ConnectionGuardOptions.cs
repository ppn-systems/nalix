// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Network.Options;

/// <summary>
/// Represents configuration options for connection protection against abuse.
/// </summary>
[IniComment("Connection protection — limits that prevent abuse (errors, packet spam, progressive bans)")]
public sealed partial class ConnectionGuardOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>
    /// Gets or sets the maximum number of concurrent connections allowed globally.
    /// </summary>
    [IniComment("Maximum concurrent connections across all IPs (default 2000)")]
    [ValueRange(1, int.MaxValue)]
    public int MaxConnections { get; set; } = 2_000;

    /// <summary>
    /// Gets or sets the ban duration for IPs that exceed the connection rate limit.
    /// </summary>
    [IniComment("How long an IP is banned after exceeding limits (00:00:01–1.00:00:00)")]
    [DurationRange("00:00:01", "1.00:00:00")]
    public System.TimeSpan BanDuration { get; set; } = System.TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the suppression window for DDoS-related log entries.
    /// </summary>
    [IniComment("Suppresses repeated DDoS log entries from the same IP within this window (00:00:01–01:00:00)")]
    [DurationRange("00:00:01", "01:00:00")]
    public System.TimeSpan DDoSLogSuppressWindow { get; set; } = System.TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets or sets the maximum number of errors a connection may accumulate within <see cref="ErrorWindow"/>
    /// before it is automatically severed.
    /// SEC-54: Prevents persistent noisy or malformed connections from consuming CPU/logs.
    /// </summary>
    [IniComment("Maximum errors allowed per connection within ErrorWindow before disconnection (SEC-54, default 50)")]
    [ValueRange(1, int.MaxValue)]
    public int MaxErrorThreshold { get; set; } = 50;

    /// <summary>
    /// Gets or sets the window over which a connection's errors are counted. When the window elapses the count
    /// restarts, so a long-lived connection with an occasional error is not disconnected cumulatively.
    /// </summary>
    [IniComment("Window over which per-connection errors are counted against MaxErrorThreshold (default 00:01:00)")]
    [DurationRange("00:00:01", "24:00:00")]
    public System.TimeSpan ErrorWindow { get; set; } = System.TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the maximum number of packets allowed per second from a single connection before it is considered abusive and disconnected.
    /// </summary>
    [IniComment("Maximum packets per second allowed from a single connection (SEC-55, default 128)")]
    [ValueRange(1, 10_000_000)]
    public int MaxPacketPerSecond { get; set; } = 128;

    /// <summary>
    /// Gets or sets a value indicating whether progressive banning is enabled.
    /// </summary>
    [IniComment("Enable progressive banning schedules (e.g. ban for 1m, then 5m, then 15m) (default true)")]
    public bool EnableProgressiveBanning { get; set; } = true;

    /// <summary>
    /// Validates the configuration options and throws an exception if validation fails.
    /// </summary>
    /// <exception cref="Abstractions.Exceptions.ValidationException">
    /// Thrown when one or more validation attributes fail.
    /// </exception>
    public void Validate() => this.ValidateDataAnnotations();
}
