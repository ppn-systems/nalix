// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading;
using Nalix.Abstractions.Injection;

namespace Nalix.Abstractions.Networking.Packets;

/// <summary>
/// Represents a context for handling network packets with support for object pooling and zero-allocation design.
/// </summary>
/// <typeparam name="TPacket">The type of packet being processed.</typeparam>
/// <remarks>
/// This class is designed to manage the lifecycle of a packet context, including initialization, property storage,
/// and cleanup for reuse in a high-performance networking environment. It uses object pooling to minimize memory
/// allocations and supports thread-safe operations.
/// </remarks>
public interface IPacketContext<TPacket> : IPoolable where TPacket : IPacket
{
    /// <summary>
    /// Gets or sets a value indicating whether the transport protocol (TCP/UDP) associated with this buffer is reliable.
    /// </summary>
    bool IsReliable { get; }

    /// <summary>
    /// Gets a value indicating whether the inbound frame this packet was deserialized from
    /// arrived encrypted on the wire. Reflects <see cref="Nalix.Abstractions.IBufferLease.EncryptedOnWire"/>
    /// as captured at decrypt time, independent of the packet header's transient flags.
    /// </summary>
    bool EncryptedOnWire { get; }

    /// <summary>
    /// If true, outbound middlewares will be skipped for this context.
    /// </summary>
    bool SkipOutbound { get; }

    /// <summary>
    /// Gets the current packet being processed.
    /// </summary>
    TPacket Packet { get; }

    /// <summary>
    /// Gets the connection associated with the packet.
    /// </summary>
    IConnection Connection { get; }

    /// <summary>
    /// Gets the packet metadata with attributes.
    /// </summary>
    PacketMetadata Attributes { get; }

    /// <summary>
    /// Gets a sender that automatically applies encryption/compression
    /// based on the current handler's attributes.
    /// Use this instead of calling connection.Transport.SendAsync() directly.
    /// </summary>
    IPacketSender Sender { get; }

    /// <summary>
    /// Gets the scoped service provider associated with the current packet lifecycle.
    /// </summary>
    IPacketScope Scope { get; }

    /// <summary>
    /// Gets or sets the cancellation token associated with the packet context.
    /// </summary>
    CancellationToken CancellationToken { get; }
}
