// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;

namespace Nalix.Abstractions.Networking;

/// <summary>
/// Represents an interface for managing a network connection.
/// </summary>
public partial interface IConnection : IDisposable, IConnectionErrorTracked
{
    /// <summary>
    /// Checks if the connection has been disposed.
    /// </summary>
    bool IsDisposed { get; }

    /// <summary>
    /// Gets the unique identifier for the connection.
    /// </summary>
    ulong ConnectionId { get; }

    /// <summary>
    /// Gets or sets the unique identifier of the authenticated user associated with this connection.
    /// Used for routing and user-specific group mapping.
    /// </summary>
    string? UserId { get; set; }

    /// <summary>
    /// Gets the total duration (in milliseconds) since the connection was established.
    /// Useful for measuring connection lifetime or session activity.
    /// </summary>
    long UpTime { get; }

    /// <summary>
    /// Gets the ping time (round-trip time) for the connection, measured in milliseconds.
    /// This value can help determine the latency of the network connection.
    /// </summary>
    long LastPingTime { get; }

    /// <summary>
    /// Gets or sets a value indicating whether idle timeout enforcement is applied to this connection.
    /// </summary>
    /// <remarks>
    /// Connections may initially be registered with the idle timeout scheduler regardless of this value.
    /// When set to <see langword="true"/>, the connection is permanently excluded from automatic
    /// idle timeout management.
    ///
    /// Once disabled, idle timeout enforcement cannot be re-enabled for the same connection.
    /// The connection lifetime must then be managed externally.
    ///
    /// This property should only be disabled for specialized scenarios such as raw passthrough
    /// transports.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    bool ExcludeFromIdleTimeout { get; set; }

    /// <summary>
    /// Gets the OP code extractor responsible for parsing incoming messages and determining their types.
    /// </summary>
    IOpCodeExtractor PacketClassifier { get; }

    /// <summary>
    /// Key identifying the endpoint associated with the connection.
    /// </summary>
    INetworkEndpoint NetworkEndpoint { get; }

    /// <summary>
    /// Gets a thread-safe extensible attribute dictionary for the connection.
    /// This can be used to store session or user-defined data associated with the connection.
    /// </summary>
    IObjectMap<AttributeKey, object> Attributes { get; }

    /// <summary>
    /// Gets the encryption key used for securing communication.
    /// </summary>
    Bytes32 Secret { get; set; }

    /// <summary>
    /// Gets the authority levels associated with the connection.
    /// </summary>
    PermissionLevel Level { get; set; }

    /// <summary>
    /// Gets or sets the encryption mode used.
    /// </summary>
    CipherSuiteType Algorithm { get; set; }

    /// <summary>
    /// Occurs when the connection is closed.
    /// </summary>
    event EventHandler<IConnectionEventArgs> ConnectionClosed;

    /// <summary>
    /// Occurs after data has been successfully processed.
    /// </summary>
    event EventHandler<IConnectionEventArgs> MessageProcessed;

    /// <summary>
    /// Occurs when data is received and processed.
    /// </summary>
    event EventHandler<IConnectionEventArgs> MessageProcessing;

    /// <summary>
    /// Disconnects the connection safely with an optional reason.
    /// </summary>
    /// <param name="reason">An optional string providing the reason for disconnection.</param>
    /// <remarks>
    /// Use this method to terminate the connection gracefully.
    /// </remarks>
    void Disconnect(string? reason = null);

    /// <summary>
    /// Dynamically updates the idle timeout for this connection.
    /// This is useful for implementing adaptive timeouts during different stages of the protocol (e.g., PoW challenge vs established).
    /// </summary>
    /// <param name="newTimeoutMs">The new timeout duration in milliseconds.</param>
    void UpdateIdleTimeout(int newTimeoutMs);
}
