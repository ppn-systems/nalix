// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Environment.Datagrams;

/// <summary>
/// Settings of one side of a datagram session. Channel count and idle timeout are negotiated in the ticket,
/// so both sides pass the same values; the receive budgets are this side's own and are advertised to the peer.
/// </summary>
public sealed class DatagramEngineOptions
{
    /// <summary>Smallest MTU, safe on IPv4 and IPv6 paths; every frame is sized to fit in it.</summary>
    public const int MinimumMtu = 1200;

    /// <summary>Largest number of channels.</summary>
    public const int MaxChannels = 64;

    /// <summary>Gets the number of channels in use (1..64). Default 8.</summary>
    public int ChannelCount { get; init; } = 8;

    /// <summary>Gets the idle timeout in milliseconds. Default 15 s.</summary>
    public int IdleTimeoutMs { get; init; } = 15_000;

    /// <summary>Gets the longest an acknowledgement is delayed, in milliseconds. Default 25 ms.</summary>
    public int MaxAckDelayMs { get; init; } = 25;

    /// <summary>Gets how long a queued unreliable message may wait before it is dropped, in milliseconds. Default 100 ms.</summary>
    public int UnreliableExpiryMs { get; init; } = 100;

    /// <summary>Gets the largest message accepted by a send or reassembled from fragments. Default 1 MiB.</summary>
    public int MaxMessageSize { get; init; } = 1024 * 1024;

    /// <summary>Gets the total bytes this side buffers for ordering and reassembly (advertised). Default 4 MiB.</summary>
    public long ReceiveBufferBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>Gets the bytes this side buffers per channel (advertised). Default 2 MiB.</summary>
    public long ChannelReceiveBufferBytes { get; init; } = 2L * 1024 * 1024;

    /// <summary>Gets the bytes that may wait in the send queue of one channel. Default 4 MiB.</summary>
    public long SendQueueBytesPerChannel { get; init; } = 4L * 1024 * 1024;

    /// <summary>Gets the largest number of fragmented messages being reassembled at once. Default 64.</summary>
    public int MaxOpenFragmentStreams { get; init; } = 64;

    /// <summary>Gets how long an incomplete fragmented message is kept, in milliseconds. Default 10 s.</summary>
    public int FragmentTimeoutMs { get; init; } = 10_000;

    /// <summary>Gets a value indicating whether the path MTU is probed above <see cref="MinimumMtu"/>. Default on.</summary>
    public bool EnableMtuProbing { get; init; } = true;

    /// <summary>Gets the number of packets sent with one key before a key update. Default 2^23.</summary>
    public long KeyUpdatePacketInterval { get; init; } = 1L << 23;

    /// <summary>Gets the time one key is used before a key update, in milliseconds. Default 1 hour.</summary>
    public long KeyUpdateIntervalMs { get; init; } = 60L * 60 * 1000;

    /// <summary>
    /// Throws when a value is out of range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(this.ChannelCount, 1, nameof(this.ChannelCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(this.ChannelCount, MaxChannels, nameof(this.ChannelCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.IdleTimeoutMs, 100, nameof(this.IdleTimeoutMs));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.MaxAckDelayMs, 1, nameof(this.MaxAckDelayMs));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.UnreliableExpiryMs, 1, nameof(this.UnreliableExpiryMs));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.MaxMessageSize, 1, nameof(this.MaxMessageSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.ChannelReceiveBufferBytes, this.MaxMessageSize + 65_536L, nameof(this.ChannelReceiveBufferBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.ReceiveBufferBytes, this.ChannelReceiveBufferBytes, nameof(this.ReceiveBufferBytes));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.SendQueueBytesPerChannel, this.MaxMessageSize, nameof(this.SendQueueBytesPerChannel));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.MaxOpenFragmentStreams, 1, nameof(this.MaxOpenFragmentStreams));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.FragmentTimeoutMs, 100, nameof(this.FragmentTimeoutMs));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.KeyUpdatePacketInterval, 16, nameof(this.KeyUpdatePacketInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(this.KeyUpdateIntervalMs, 1000, nameof(this.KeyUpdateIntervalMs));
    }
}
