// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.SDK.Options;

/// <summary>
/// Client transport configuration loaded by the ConfigurationManager.
/// Place appropriate keys in your configuration source under the section
/// matching this class name (TransportOptions).
/// </summary>
[IniComment("Client transport configuration — controls connectivity, reconnect policy, socket tuning, and encryption")]
public sealed partial class TransportOptions : ConfigurationLoader
{
    /// <summary>
    /// Gets the port number for the connection.
    /// </summary>
    [IniComment("Server port to connect to (1–65535)")]
    [ValueRange(1, 65535)]
    public ushort Port { get; set; } = 57206;

    /// <summary>
    /// Gets the server address or hostname.
    /// </summary>
    [IniComment("Server IP address or hostname")]
    [Length(1)]
    public string Address { get; set; } = "127.0.0.1";

    // Basic connectivity

    /// <summary>
    /// Timeout for connect attempts in milliseconds. A value of 0 means no timeout.
    /// </summary>
    [IniComment("Connect attempt timeout in milliseconds (0 = no timeout)")]
    [ValueRange(0, int.MaxValue)]
    public int ConnectTimeoutMillis { get; set; } = 30000;

    // Socket tuning

    /// <summary>
    /// Controls the TCP_NODELAY socket option. When true, Nagle's algorithm is disabled.
    /// </summary>
    [IniComment("Disable Nagle's algorithm for lower latency (recommended: true)")]
    public bool NoDelay { get; set; } = true;

    /// <summary>
    /// Size (in bytes) of the socket send and receive buffer.
    /// </summary>
    [IniComment("Socket send and receive buffer size in bytes")]
    [ValueRange(2048, 1048576)]
    public int BufferSize { get; set; } = 65536;

    /// <summary>
    /// Gets the encryption mode for the connection.
    /// </summary>
    [IniComment("Cipher suite used for packet encryption (e.g., Chacha20, Salsa20, Chacha20Poly1305, Salsa20Poly1305)")]
    public CipherSuiteType Algorithm { get; set; } = CipherSuiteType.Chacha20Poly1305;

    /// <summary>
    /// When true, LZ4 compression is applied to packets exceeding the <see cref="CompressionThreshold"/>.
    /// </summary>
    [IniComment("Enable LZ4 compression for outbound packets")]
    public bool CompressionEnabled { get; set; } = true;

    /// <summary>
    /// The minimum size in bytes for a packet to be considered for compression.
    /// </summary>
    [IniComment("Minimum size in bytes to trigger compression")]
    public int CompressionThreshold { get; set; } = 512;

    /// <summary>
    /// [Reserved for future use] Gets or sets the capacity of the asynchronous message processing queue. Currently not consumed.
    /// SEC-56, SEC-59: Provides backpressure to prevent memory exhaustion under high load.
    /// </summary>
    [IniComment("[Reserved] Capacity of the asynchronous message processing queue (default 1024, currently not consumed)")]
    [ValueRange(1, 65536)]
    public int AsyncQueueCapacity { get; set; } = 1024;

    /// <summary>
    /// The maximum size (in bytes) allowed for a single UDP datagram (including the 8-byte Token).
    /// </summary>
    [IniComment("Maximum allowed UDP datagram size in bytes (including header). Default 1400.")]
    [ValueRange(64, 65507)]
    public int MaxUdpDatagramSize { get; set; } = 1400;

    /// <summary>
    /// Gets or sets the pinned Server Identity Public Key (required for protecting against MitM attacks).
    /// </summary>
    [IniComment("Pinned X25519 Public Key representation in HEX for Identity Authentication (MitM protection).")]
    public string? ServerPublicKey { get; set; }

    /// <summary>
    /// Gets or sets whether Trust-On-First-Use (TOFU) public key fetching is allowed when <see cref="ServerPublicKey"/> is not set.
    /// </summary>
    [IniComment("Allow Trust-On-First-Use (TOFU) public key discovery when ServerPublicKey is not configured")]
    public bool AllowTofu { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a TOFU-fetched server public key should be persisted to disk after handshake verification succeeds.
    /// </summary>
    [IniComment("Persist TOFU-discovered ServerPublicKey to configuration storage after a verified handshake")]
    public bool PersistTofuKey { get; set; } = false;

    /// <summary>
    /// Gets or sets whether the SDK should attempt a session resume before performing a new handshake.
    /// </summary>
    [IniComment("Attempt session resume before doing a fresh handshake")]
    public bool ResumeEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the resume request timeout in milliseconds.
    /// </summary>
    [IniComment("Timeout in milliseconds for session resume requests")]
    [ValueRange(100, int.MaxValue)]
    public int ResumeTimeoutMillis { get; set; } = 3000;

    /// <summary>
    /// Gets or sets whether a failed resume should fall back to a fresh handshake.
    /// </summary>
    [IniComment("Fall back to a full handshake when resume fails")]
    public bool ResumeFallbackToHandshake { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the SDK should automatically reconnect when the transport disconnects unexpectedly.
    /// </summary>
    [IniComment("Automatically reconnect when the transport disconnects unexpectedly")]
    public bool AutoReconnectEnabled { get; set; }

    /// <summary>
    /// Gets or sets the base delay in milliseconds before the first reconnect attempt.
    /// Subsequent attempts back off exponentially, capped at <see cref="ReconnectMaxDelayMillis"/>.
    /// </summary>
    [IniComment("Base delay in milliseconds before the first reconnect attempt")]
    [ValueRange(1, int.MaxValue)]
    public int ReconnectBaseDelayMillis { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum delay in milliseconds between reconnect attempts.
    /// </summary>
    [IniComment("Maximum delay in milliseconds between reconnect attempts (exponential backoff cap)")]
    [ValueRange(1, int.MaxValue)]
    public int ReconnectMaxDelayMillis { get; set; } = 30000;

    /// <summary>
    /// Gets or sets the maximum number of consecutive reconnect attempts before giving up.
    /// A value of 0 means unlimited attempts.
    /// </summary>
    [IniComment("Maximum number of consecutive reconnect attempts before giving up (0 = unlimited)")]
    [ValueRange(0, int.MaxValue)]
    public int ReconnectMaxAttempts { get; set; }

    /// <summary>
    /// Gets or sets the periodic keepalive PING interval in milliseconds.
    /// A value of 0 (the default) disables automatic keepalive probing.
    /// </summary>
    [IniComment("Periodic keepalive PING interval in milliseconds (0 = disabled)")]
    [ValueRange(0, int.MaxValue)]
    public int KeepAliveIntervalMillis { get; set; }

    /// <summary>
    /// Gets or sets the timeout in milliseconds to wait for each keepalive PONG response.
    /// </summary>
    [IniComment("Timeout in milliseconds for each keepalive PONG response")]
    [ValueRange(100, int.MaxValue)]
    public int KeepAliveTimeoutMillis { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the number of consecutive missed keepalive PONG responses before the
    /// transport is treated as dead and torn down so <c>ReconnectSupervisor</c> can recover it.
    /// </summary>
    [IniComment("Consecutive missed keepalive PONG responses before declaring the connection dead")]
    [ValueRange(1, int.MaxValue)]
    public int KeepAliveMissThreshold { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether the internal global clock can be synchronized with the server's time during SyncTimeAsync.
    /// Disable this if the connection is untrusted to prevent cross-session clock skew.
    /// </summary>
    [IniComment("Allow global clock synchronization from this session (recommended: true for trusted servers)")]
    public bool TimeSyncEnabled { get; set; } = true;

    /// <summary>
    /// Gets the current time offset in milliseconds applied by the time synchronization process.
    /// </summary>
    [IniComment("Current time offset in milliseconds applied by the time synchronization process")]
    public double TimeOffsetMs { get; set; }
}
