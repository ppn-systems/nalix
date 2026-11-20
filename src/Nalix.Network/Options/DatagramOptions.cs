// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;
using Nalix.Environment.Datagrams;

namespace Nalix.Network.Options;

/// <summary>
/// Settings of the datagram (NDP) listener and of the sessions it accepts.
/// </summary>
[IniComment("Datagram sessions (NDP over UDP) — limits advertised in tickets and listener settings")]
public sealed partial class DatagramOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>Gets or sets the seconds within which the first datagram of a ticket must arrive. Default 10.</summary>
    [IniComment("Seconds a ticket waits for its first datagram (default 10)")]
    [ValueRange(1, 300)]
    public int TicketLifetimeSeconds { get; set; } = 10;

    /// <summary>Gets or sets the number of channels per session (1..64). Default 8.</summary>
    [IniComment("Channels per datagram session, 1-64 (default 8)")]
    [ValueRange(1, DatagramEngineOptions.MaxChannels)]
    public int ChannelCount { get; set; } = 8;

    /// <summary>Gets or sets the idle timeout in milliseconds. Default 15 000.</summary>
    [IniComment("Idle timeout of a datagram session in milliseconds (default 15000)")]
    [ValueRange(1_000, 600_000)]
    public int IdleTimeoutMs { get; set; } = 15_000;

    /// <summary>Gets or sets the largest message, in bytes. Default 1 MiB.</summary>
    [IniComment("Largest message in bytes (default 1048576)")]
    [ValueRange(1_024, 16 * 1024 * 1024)]
    public int MaxMessageSize { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the total receive budget per session, in bytes. Default 4 MiB.</summary>
    [IniComment("Bytes a session buffers for ordering and reassembly (default 4194304)")]
    [ValueRange(65_536, int.MaxValue)]
    public long ReceiveBufferBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Gets or sets the receive budget per channel, in bytes. Default 2 MiB.</summary>
    [IniComment("Bytes a session buffers per channel (default 2097152)")]
    [ValueRange(65_536, int.MaxValue)]
    public long ChannelReceiveBufferBytes { get; set; } = 2L * 1024 * 1024;

    /// <summary>Gets or sets how long a queued unreliable message may wait, in milliseconds. Default 100.</summary>
    [IniComment("Milliseconds a queued unreliable message may wait before it is dropped (default 100)")]
    [ValueRange(1, 10_000)]
    public int UnreliableExpiryMs { get; set; } = 100;

    /// <summary>Gets or sets the largest number of sessions and pending tickets. Default 100 000.</summary>
    [IniComment("Largest number of datagram sessions plus pending tickets (default 100000)")]
    [ValueRange(1, int.MaxValue)]
    public int MaxSessions { get; set; } = 100_000;

    /// <summary>Gets or sets the socket receive buffer size, in bytes. Default 4 MiB.</summary>
    [IniComment("UDP socket receive buffer size in bytes (default 4194304)")]
    [ValueRange(65_536, int.MaxValue)]
    public int SocketReceiveBufferSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Gets or sets the socket send buffer size, in bytes. Default 4 MiB.</summary>
    [IniComment("UDP socket send buffer size in bytes (default 4194304)")]
    [ValueRange(65_536, int.MaxValue)]
    public int SocketSendBufferSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Creates the engine options of one session from these settings.
    /// </summary>
    public DatagramEngineOptions ToEngineOptions() => new()
    {
        ChannelCount = this.ChannelCount,
        IdleTimeoutMs = this.IdleTimeoutMs,
        MaxMessageSize = this.MaxMessageSize,
        ReceiveBufferBytes = this.ReceiveBufferBytes,
        ChannelReceiveBufferBytes = this.ChannelReceiveBufferBytes,
        UnreliableExpiryMs = this.UnreliableExpiryMs,
    };

    /// <inheritdoc/>
    public void Validate()
    {
        this.ValidateDataAnnotations();
        this.ToEngineOptions().Validate();
    }
}
