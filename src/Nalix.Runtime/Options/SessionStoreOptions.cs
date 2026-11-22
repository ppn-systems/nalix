// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Runtime.Options;

/// <summary>
/// Store options for resumable sessions, controlling how long inactive sessions are retained before expiration.
/// This applies after a connection is gone; live connection idle reaping is controlled by
/// <c>TimingWheelOptions.IdleTimeoutMs</c> instead.
/// </summary>
[IniComment("Session store configuration — controls retention of resumable sessions and their expiration")]
public sealed partial class SessionStoreOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>
    /// Gets or sets the time-to-live for resumable sessions.
    /// This does not keep an idle live connection open; it only retains disconnected session state for resume.
    /// </summary>
    [IniComment("Duration after which an inactive session expires (default 5m)")]
    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the minimum number of attributes in the ObjectMap required to persist a session.
    /// This helps prevent DDoS by not saving "empty" sessions from handshake-only connections.
    /// </summary>
    [IniComment("Minimum number of attributes required to persist a session (excluding internal flags, default 10)")]
    [ValueRange(0, int.MaxValue)]
    public int MinAttributesForPersistence { get; set; } = 10;

    /// <summary>
    /// Validates the configuration options.
    /// </summary>
    public void Validate()
    {
        this.ValidateDataAnnotations();

        if (this.SessionTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(this.SessionTtl), "SessionTtl must be a positive time duration.");
        }

        if (this.MinAttributesForPersistence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(this.MinAttributesForPersistence), "MinAttributesForPersistence cannot be negative.");
        }
    }
}
