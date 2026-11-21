// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Network.Options;

/// <summary>
/// Represents network configuration settings for WebSocket connections.
/// </summary>
[IniComment("Network WebSocket configuration — controls endpoint, subprotocol, and behavior")]
public sealed partial class NetworkWebSocketOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>
    /// Gets or sets the port number for the WebSocket connection.
    /// </summary>
    [IniComment("WebSocket port to listen on (1–65535, default 57207)")]
    [ValueRange(1, 65535)]
    public ushort Port { get; set; } = 57207;

    /// <summary>
    /// Gets or sets the WebSocket path.
    /// </summary>
    [IniComment("WebSocket endpoint path (default /ws/)")]
    public string Path { get; set; } = "/ws/";

    /// <summary>
    /// Gets or sets the host to bind the listener to (e.g., *, +, localhost).
    /// </summary>
    [IniComment("Host address to bind to (default *)")]
    public string Host { get; set; } = "*";

    /// <summary>
    /// Gets or sets the subprotocol to negotiate.
    /// </summary>
    [IniComment("WebSocket subprotocol identifier (default nalix.v1)")]
    public string SubProtocol { get; set; } = "nalix.v1";

    /// <summary>
    /// Gets or sets the custom server version string exposed via the /version HTTP endpoint.
    /// </summary>
    [IniComment("Custom server version exposed via /version endpoint (default 1.0.0)")]
    public string ServerVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Gets or sets a value indicating whether native DevOps HTTP endpoints (/healthz, /version, /metrics) are enabled.
    /// Default is true.
    /// </summary>
    [IniComment("Enable native HTTP endpoints (/healthz, /version, /metrics) (default true)")]
    public bool EnableDevOpsEndpoints { get; set; } = true;

    /// <summary>
    /// Gets or sets the bearer token required to read the DevOps endpoints (/metrics, /version).
    /// Empty (the default) disables both endpoints.
    /// </summary>
    [IniComment("Bearer token required for /metrics and /version; empty disables both endpoints (default empty)")]
    public string DevOpsBearerToken { get; set; } = string.Empty;

    /// <summary>
    /// Maximum time in milliseconds to wait for the HTTP upgrade handshake to complete
    /// before closing the socket. Prevents slowloris-style attacks.
    /// </summary>
    [IniComment("Maximum handshake wait time in ms (default 5000)")]
    [ValueRange(500, 30000)]
    public int HandshakeTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Maximum size in bytes for the HTTP upgrade request headers.
    /// Requests exceeding this limit are rejected immediately.
    /// </summary>
    [IniComment("Maximum HTTP upgrade request size in bytes (default 8192)")]
    [ValueRange(1024, 65536)]
    public int MaxUpgradeRequestSize { get; set; } = 8192;

    /// <summary>
    /// Maximum inbound WebSocket message size in bytes.
    /// </summary>
    [IniComment("Maximum inbound WebSocket message size in bytes (default 1048576)")]
    [ValueRange(1, int.MaxValue)]
    public int MaxMessageSize { get; set; } = 1_048_576;

    /// <summary>
    /// Gets or sets the protocol-level keep-alive ping interval in seconds for WebSocket connections.
    /// Default is 30 seconds.
    /// </summary>
    [IniComment("WebSocket protocol-level keep-alive ping interval in seconds (default 30)")]
    [ValueRange(0, 3600)]
    public int KeepAliveIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Comma-separated allowlist of browser origins (scheme+host+port) permitted to open a
    /// WebSocket handshake, e.g. <c>https://schools.eyc.education,https://app.example.com</c>.
    /// Empty disables the check (legacy behavior). Guards against Cross-Site WebSocket Hijacking (CSWSH).
    /// </summary>
    [IniComment("Comma-separated allowed browser origins for CSWSH protection; empty disables the check (default empty)")]
    public string AllowedOrigins { get; set; } = string.Empty;

    /// <summary>
    /// When an allowlist is configured, controls whether a handshake without an <c>Origin</c> header
    /// (non-browser/native clients) is permitted. Ignored when <see cref="AllowedOrigins"/> is empty.
    /// </summary>
    [IniComment("Allow handshakes with no Origin header (native clients) when an allowlist is set (default true)")]
    public bool AllowMissingOrigin { get; set; } = true;

    // ponytail: cache is rebuilt when the source string reference changes; races between accept
    // workers are benign (idempotent rebuild). Upgrade to a lock only if origins become mutable at runtime.
    private string? _originsSource;
    private string[] _originsCache = [];

    /// <summary>
    /// Determines whether a handshake carrying the given <c>Origin</c> header value is allowed.
    /// </summary>
    /// <param name="origin">The raw <c>Origin</c> header value, or <c>null</c> when absent.</param>
    /// <returns><c>true</c> if the handshake may proceed; otherwise <c>false</c>.</returns>
    internal bool IsOriginAllowed(string? origin)
    {
        string[] allow = this.GetAllowedOriginsCache();

        // Empty allowlist => feature disabled, legacy behavior (cheapest path, no allocation).
        if (allow.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(origin))
        {
            return this.AllowMissingOrigin;
        }

        System.ReadOnlySpan<char> normalized = origin.AsSpan().Trim().TrimEnd('/');
        for (int i = 0; i < allow.Length; i++)
        {
            if (normalized.Equals(allow[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string[] GetAllowedOriginsCache()
    {
        string source = this.AllowedOrigins ?? string.Empty;
        if (!ReferenceEquals(source, _originsSource))
        {
            _originsCache = ParseOrigins(source);
            _originsSource = source;
        }

        return _originsCache;
    }

    private static string[] ParseOrigins(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        string[] parts = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].TrimEnd('/');
        }

        return parts;
    }

    /// <summary>
    /// Validates the configuration options and throws an exception if validation fails.
    /// </summary>
    /// <exception cref="ValidationException">
    /// Thrown when one or more validation attributes fail, or when <see cref="DevOpsBearerToken"/> is set but shorter than 16 characters.
    /// </exception>
    public void Validate()
    {
        this.ValidateDataAnnotations();

        if (!string.IsNullOrEmpty(this.DevOpsBearerToken) && this.DevOpsBearerToken.Length < 16)
        {
            throw new ValidationException("DevOpsBearerToken must be at least 16 characters when set.");
        }
    }
}
