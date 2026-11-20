// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Environment.Datagrams;

/// <summary>
/// The receive budgets the peer advertised, first in the ticket and later in LIMITS frames.
/// A sender keeps its unreleased reliable bytes within them.
/// </summary>
/// <param name="ReceiveBufferBytes">Total bytes the peer buffers for ordering and reassembly.</param>
/// <param name="ChannelReceiveBufferBytes">Bytes the peer buffers per channel.</param>
public readonly record struct DatagramPeerLimits(long ReceiveBufferBytes, long ChannelReceiveBufferBytes)
{
    /// <summary>
    /// Gets the limits of a peer that uses the default <see cref="DatagramEngineOptions"/>.
    /// </summary>
    public static DatagramPeerLimits Default { get; } = From(new DatagramEngineOptions());

    /// <summary>
    /// Gets the limits a side with <paramref name="options"/> advertises.
    /// </summary>
    public static DatagramPeerLimits From(DatagramEngineOptions options)
    {
        System.ArgumentNullException.ThrowIfNull(options);
        return new(options.ReceiveBufferBytes, options.ChannelReceiveBufferBytes);
    }
}
