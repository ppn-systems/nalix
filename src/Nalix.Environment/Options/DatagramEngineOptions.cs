// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Environment.Options;

/// <summary>
/// Settings of one side of a datagram (NDP) session. The server advertises channel count, idle timeout,
/// message size and receive budgets in the ticket, and the client uses the same values.
/// </summary>
[IniComment("Datagram session engine (NDP) — channels, timeouts, receive budgets and key update")]
public sealed partial class DatagramEngineOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>Smallest MTU, safe on IPv4 and IPv6 paths; every frame is sized to fit in it.</summary>
    public const int MinimumMtu = 1200;

    /// <summary>Largest number of channels.</summary>
    public const int MaxChannels = 64;

    /// <summary>Gets or sets the number of channels in use (1..64). Default 8.</summary>
    [IniComment("Channels per session, 1-64 (default 8)")]
    [ValueRange(1, MaxChannels)]
    public int ChannelCount { get; set; } = 8;

    /// <summary>Gets or sets the idle timeout in milliseconds. Default 15 s.</summary>
    [IniComment("Idle timeout in milliseconds (default 15000)")]
    [ValueRange(100, 3_600_000)]
    public int IdleTimeoutMs { get; set; } = 15_000;

    /// <summary>Gets or sets the longest an acknowledgement is delayed, in milliseconds. Default 25 ms.</summary>
    [IniComment("Longest acknowledgement delay in milliseconds (default 25)")]
    [ValueRange(1, 1_000)]
    public int MaxAckDelayMs { get; set; } = 25;

    /// <summary>Gets or sets how long a queued unreliable message may wait before it is dropped, in milliseconds. Default 100 ms.</summary>
    [IniComment("Milliseconds a queued unreliable message may wait before it is dropped (default 100)")]
    [ValueRange(1, 60_000)]
    public int UnreliableExpiryMs { get; set; } = 100;

    /// <summary>Gets or sets the largest message accepted by a send or reassembled from fragments. Default 1 MiB.</summary>
    [IniComment("Largest message in bytes (default 1048576)")]
    [ValueRange(1, 64 * 1024 * 1024)]
    public int MaxMessageSize { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the total bytes this side buffers for ordering and reassembly (advertised). Default 4 MiB.</summary>
    [IniComment("Bytes buffered for ordering and reassembly per session, advertised to the peer (default 4194304)")]
    [ValueRange(1L, long.MaxValue)]
    public long ReceiveBufferBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Gets or sets the bytes this side buffers per channel (advertised). Default 2 MiB.</summary>
    [IniComment("Bytes buffered per channel, advertised to the peer (default 2097152)")]
    [ValueRange(1L, long.MaxValue)]
    public long ChannelReceiveBufferBytes { get; set; } = 2L * 1024 * 1024;

    /// <summary>Gets or sets the bytes that may wait in the send queue of one channel. Default 4 MiB.</summary>
    [IniComment("Bytes that may wait in the send queue of one channel (default 4194304)")]
    [ValueRange(1L, long.MaxValue)]
    public long SendQueueBytesPerChannel { get; set; } = 4L * 1024 * 1024;

    /// <summary>Gets or sets the largest number of fragmented messages being reassembled at once. Default 64.</summary>
    [IniComment("Largest number of fragmented messages reassembled at once (default 64)")]
    [ValueRange(1, 65_535)]
    public int MaxOpenFragmentStreams { get; set; } = 64;

    /// <summary>Gets or sets how long an incomplete fragmented message is kept, in milliseconds. Default 10 s.</summary>
    [IniComment("Milliseconds an incomplete fragmented message is kept (default 10000)")]
    [ValueRange(100, 3_600_000)]
    public int FragmentTimeoutMs { get; set; } = 10_000;

    /// <summary>Gets or sets a value indicating whether the path MTU is probed above <see cref="MinimumMtu"/>. Default on.</summary>
    [IniComment("Probe the path MTU above 1200 bytes (default true)")]
    public bool EnableMtuProbing { get; set; } = true;

    /// <summary>Gets or sets the number of packets sent with one key before a key update. Default 2^23.</summary>
    [IniComment("Packets sent with one key before a key update (default 8388608)")]
    [ValueRange(16L, 1L << 40)]
    public long KeyUpdatePacketInterval { get; set; } = 1L << 23;

    /// <summary>Gets or sets the time one key is used before a key update, in milliseconds. Default 1 hour.</summary>
    [IniComment("Milliseconds one key is used before a key update (default 3600000)")]
    [ValueRange(1_000L, 7L * 24 * 60 * 60 * 1000)]
    public long KeyUpdateIntervalMs { get; set; } = 60L * 60 * 1000;

    /// <summary>
    /// Validates the options.
    /// </summary>
    /// <exception cref="ValidationException">A value is out of range, or the budgets cannot hold the largest message.</exception>
    public void Validate()
    {
        this.ValidateDataAnnotations();

        // A fragmented message is released by the peer only once it is complete, so one must fit in a channel
        // budget together with its fragment headers and per-message overhead.
        long largestMessageCost = this.MaxMessageSize + 65_536L;
        if (this.ChannelReceiveBufferBytes < largestMessageCost)
        {
            throw new ValidationException(
                $"ChannelReceiveBufferBytes={this.ChannelReceiveBufferBytes} must be at least MaxMessageSize + 65536 ({largestMessageCost}).");
        }

        if (this.ReceiveBufferBytes < this.ChannelReceiveBufferBytes)
        {
            throw new ValidationException(
                $"ReceiveBufferBytes={this.ReceiveBufferBytes} must be >= ChannelReceiveBufferBytes={this.ChannelReceiveBufferBytes}.");
        }

        if (this.SendQueueBytesPerChannel < this.MaxMessageSize)
        {
            throw new ValidationException(
                $"SendQueueBytesPerChannel={this.SendQueueBytesPerChannel} must be >= MaxMessageSize={this.MaxMessageSize}.");
        }
    }
}
