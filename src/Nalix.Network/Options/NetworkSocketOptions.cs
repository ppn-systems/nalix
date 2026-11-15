// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Network.Options;

/// <summary>
/// Represents network configuration settings for socket and TCP connections.
/// </summary>
[IniComment("Network socket configuration — controls port, buffering, concurrency, and socket behavior")]
public sealed partial class NetworkSocketOptions : ConfigurationLoader, IValidatableConfiguration
{
    #region Constants

    internal const int True = 1;
    internal const int False = 0;

    #endregion Constants

    #region Properties

    /// <summary>
    /// Gets or sets the port number for the network connection.
    /// </summary>
    /// <exception cref="System.ArgumentOutOfRangeException"></exception>
    [IniComment("TCP port to listen on (1–65535, default 57206)")]
    [ValueRange(1, 65535)]
    public ushort Port
    {
        get;
        set
        {
            if (value < 1)
            {
                throw new System.ArgumentOutOfRangeException(nameof(value), "Port must be at least 1 (0 is not allowed).");
            }

            field = value;
        }
    } = 57206;

    /// <summary>
    /// Gets or sets the maximum length of the pending connections queue.
    /// </summary>
    [IniComment("Maximum pending connection queue length (1–65535)")]
    [ValueRange(1, 65535)]
    public int Backlog { get; set; } = 512;

    /// <summary>
    /// Gets or sets a value indicating whether the idle timeout mechanism is enabled.
    /// </summary>
    [IniComment("Enable idle connection timeout enforcement")]
    public bool EnableTimeout { get; set; } = true;

    /// <summary>
    /// Indicates whether to use IPv6 instead of IPv4.
    /// </summary>
    [IniComment("Listen on IPv6 instead of IPv4")]
    public bool EnableDualStack { get; set; } = true;

    /// <summary>
    /// Gets or sets whether Nagle's algorithm is disabled (low-latency mode).
    /// </summary>
    [IniComment("Disable Nagle's algorithm for lower latency (recommended: true)")]
    public bool NoDelay { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of parallel connections.
    /// </summary>
    [IniComment("Maximum simultaneous parallel listeners/acceptors (1–1024, default 1)")]
    [ValueRange(1, 1024)]
    public int MaxParallel { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum number of parallel connections.
    /// </summary>
    [IniComment("Maximum simultaneous parallel listeners/acceptors (1–1024, default 1)")]
    [ValueRange(1, 1024)]
    public int MaxParallelUDP { get; set; } = 1;

    /// <summary>
    /// Gets or sets the buffer size for both sending and receiving data.
    /// </summary>
    [IniComment("Send and receive buffer size in bytes (1024–10,485,760)")]
    [ValueRange(2048, 10_485_760)]
    public int BufferSize { get; set; } = 65536;

    /// <summary>
    /// Gets or sets a value indicating whether TCP Keep-Alive is enabled.
    /// </summary>
    [IniComment("Enable TCP Keep-Alive probes to detect dead connections")]
    public bool KeepAlive { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the TCP listener may reuse an address in TIME_WAIT state.
    /// UDP uses <see cref="UdpReuseAddress"/> instead.
    /// </summary>
    [IniComment("Allow reuse of a local address in TIME_WAIT state (recommended: true)")]
    public bool ReuseAddress { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the UDP listener may share its address with other sockets (SO_REUSEADDR).
    /// Disabled by default: sharing lets another local process bind the same port and receive datagrams.
    /// Ignored on Windows, where the UDP listener always uses exclusive address use.
    /// </summary>
    [IniComment("Allow another socket to share the UDP listener address (SO_REUSEADDR); unsafe on shared hosts, ignored on Windows (default: false)")]
    public bool UdpReuseAddress { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the DualMode feature is enabled (for IPv6 sockets).
    /// </summary>
    [IniComment("Enable DualMode to support both IPv4 and IPv6 connections on the same socket (defaults: true for IPv6)")]
    public bool DualMode { get; set; } = true;

    /// <summary>
    /// CPU core index (0-based) to pin the TCP dispatch worker thread.
    /// Leave as -1 for OS default scheduling (no pinning).
    /// </summary>
    [IniComment("CPU core index (0-based) to pin the TCP dispatch worker thread (leave as -1 for OS default)")]
    public int DispatchProcessorAffinity { get; set; } = -1;

    /// <summary>
    /// Maximum accepted connections that may queue in the channel while the consumer
    /// thread is busy.
    /// <para>
    /// Tune to roughly <c>2 × burst rate × ProcessConnection latency (ms)</c>.
    /// Default 256 matches the typical TCP backlog.
    /// </para>
    /// </summary>
    [IniComment("Maximum accepted connections that may queue in the channel while the consumer thread is busy (tune to ~2 × burst rate × ProcessConnection latency in ms, default 128)")]
    [ValueRange(1, int.MaxValue)]
    public int ProcessChannelCapacity { get; set; } = 256;

    /// <summary>
    /// Gets or sets the maximum time in milliseconds to wait for the process channel to drain
    /// gracefully during shutdown before forceful termination.
    /// </summary>
    [IniComment("Maximum time in milliseconds to wait for the process channel to drain gracefully during shutdown (default: 5000)")]
    [ValueRange(0, 60000)]
    public int ProcessChannelDrainTimeout { get; set; } = 5000;

    /// <summary>
    /// Gets or sets whether the TCP listener may share its port with other sockets (SO_REUSEPORT on Linux).
    /// Disabled by default: on Linux a process running under the same user can otherwise bind the same port
    /// and receive a share of incoming connections. Enable only for an explicit multi-socket load-balancing setup.
    /// </summary>
    [IniComment("Allow multiple sockets to bind to the same port for load balancing; can let another local process of the same user share the port (default: false)")]
    public bool ReusePort { get; set; } = false;

    /// <summary>
    /// TcpFastOpen enables the TCP Fast Open feature, which allows data to be sent during the initial connection handshake, reducing latency for subsequent connections.
    /// </summary>
    [IniComment("Enable TCP Fast Open to reduce latency for subsequent connections (default: true)")]
    public bool TcpFastOpen { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum size (in bytes) allowed for a single UDP datagram.
    /// Default 1440 avoids IP fragmentation.
    /// </summary>
    [IniComment("Maximum allowed UDP datagram size in bytes to avoid fragmentation (default 1440)")]
    [ValueRange(64, 65507)]
    public int MaxUdpDatagramSize { get; set; } = 1440;

    #endregion Properties

    /// <summary>
    /// Validates the configuration options and throws an exception if validation fails.
    /// </summary>
    /// <exception cref="Abstractions.Exceptions.ValidationException">
    /// Thrown when one or more validation attributes fail.
    /// </exception>
    public void Validate() => this.ValidateDataAnnotations();
}
