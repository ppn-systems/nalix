// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Network.Options;

/// <summary>
/// Represents configuration options for Proxy Protocol V1/V2 parsing.
/// </summary>
[IniComment("Proxy Protocol configuration — handling real IP extraction behind load balancers")]
public sealed partial class ProxyProtocolOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>
    /// Enable Proxy Protocol V1/V2 header parsing to extract the real client IP.
    /// Must be true when the server sits behind HAProxy, AWS NLB, or any proxy
    /// that injects a PROXY header. Requires <see cref="RequireTrustedProxy"/> to be true;
    /// <see cref="Validate"/> rejects the combination Enabled=true, RequireTrustedProxy=false.
    /// </summary>
    [IniComment("Enable Proxy Protocol V1/V2 parsing to obtain the real client IP (default: false)")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When true, only connections originating from IPs listed in the Trusted Proxies list
    /// are allowed to perform a Proxy Protocol header read. All other connections are dropped.
    /// Must be true whenever <see cref="Enabled"/> is true (enforced by <see cref="Validate"/>).
    /// An empty trusted list rejects every connection while this is true (fail closed).
    /// </summary>
    [IniComment("Drop Proxy Protocol headers from untrusted physical IPs. Must be true when Enabled is true (default: false)")]
    public bool RequireTrustedProxy { get; set; } = false;

    /// <summary>
    /// Maximum milliseconds to wait for the PROXY header to arrive after TCP accept.
    /// Connections that do not deliver a complete header within this window are dropped.
    /// Tune to roughly RTT_max + 50ms. Default 2000ms.
    /// </summary>
    [IniComment("Timeout in ms for the PROXY header read (default: 2000)")]
    [ValueRange(100, 30000)]
    public int HeaderTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// Maximum number of in-flight connections waiting for a PROXY header.
    /// This prevents DDoS attacks that exhaust server memory by holding connections open.
    /// </summary>
    [IniComment("Maximum concurrent connections waiting for a Proxy Protocol header (default: 1024)")]
    [ValueRange(1, 100_000)]
    public int MaxPendingProxyConnections { get; set; } = 1024;

    /// <summary>
    /// Validates the configuration options and throws an exception if validation fails.
    /// </summary>
    /// <exception cref="Abstractions.Exceptions.ValidationException">
    /// Thrown when one or more validation attributes fail.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <see cref="Enabled"/> is <see langword="true"/> while
    /// <see cref="RequireTrustedProxy"/> is <see langword="false"/>. Accepting PROXY headers from
    /// untrusted peers lets any client spoof its source address and bypass bans and per-IP limits.
    /// </exception>
    public void Validate()
    {
        this.ValidateDataAnnotations();

        // Cross-field guard: PROXY headers must only be read from trusted proxies.
        if (this.Enabled && !this.RequireTrustedProxy)
        {
            throw new System.ArgumentException(
                $"{nameof(this.RequireTrustedProxy)} must be true when {nameof(this.Enabled)} is true. " +
                "Accepting PROXY headers from untrusted peers allows clients to spoof their IP address.");
        }
    }
}
