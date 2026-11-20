// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Network.Options;

/// <summary>
/// Settings of the datagram (NDP) listener. The settings of each session are in
/// <see cref="Nalix.Environment.Options.DatagramEngineOptions"/>.
/// </summary>
[IniComment("Datagram listener (NDP over UDP) — tickets, session limit and socket buffers")]
public sealed partial class DatagramOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>Gets or sets the seconds within which the first datagram of a ticket must arrive. Default 10.</summary>
    [IniComment("Seconds a ticket waits for its first datagram (default 10)")]
    [ValueRange(1, 300)]
    public int TicketLifetimeSeconds { get; set; } = 10;

    /// <summary>Gets or sets the largest number of sessions and pending tickets. Default 100 000.</summary>
    [IniComment("Largest number of datagram sessions plus pending tickets (default 100000)")]
    [ValueRange(1, int.MaxValue)]
    public int MaxSessions { get; set; } = 100_000;

    /// <summary>Gets or sets the number of receive workers reading the socket. Default 1.</summary>
    [IniComment("Receive workers reading the UDP socket, 1-64 (default 1)")]
    [ValueRange(1, 64)]
    public int ReceiveWorkers { get; set; } = 1;

    /// <summary>Gets or sets the socket receive buffer size, in bytes. Default 4 MiB.</summary>
    [IniComment("UDP socket receive buffer size in bytes (default 4194304)")]
    [ValueRange(65_536, int.MaxValue)]
    public int SocketReceiveBufferSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Gets or sets the socket send buffer size, in bytes. Default 4 MiB.</summary>
    [IniComment("UDP socket send buffer size in bytes (default 4194304)")]
    [ValueRange(65_536, int.MaxValue)]
    public int SocketSendBufferSize { get; set; } = 4 * 1024 * 1024;

    /// <inheritdoc/>
    public void Validate() => this.ValidateDataAnnotations();
}
