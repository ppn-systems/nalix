// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Abstractions.Networking;

/// <summary>
/// Provides a centralized set of well-known connection attribute keys used throughout the Nalix framework.
/// </summary>
public static class ConnectionAttributes
{
    /// <summary>
    /// Key for the connection hub that owns this connection.
    /// </summary>
    public static readonly AttributeKey OwnerHub = AttributeKey.FromName("nalix.owner_hub");

    /// <summary>
    /// Key for the shared sequence numbers state stored during the connection lifecycle.
    /// </summary>
    public static readonly AttributeKey SequenceState = AttributeKey.FromName("nalix.connection.sequence_state");

    /// <summary>
    /// Key for the runtime specific state stored during the connection lifecycle.
    /// </summary>
    public static readonly AttributeKey RuntimeState = AttributeKey.FromName("nalix.connection.runtime_state");

    /// <summary>
    /// Key for the one-time extension, in milliseconds (<see cref="int"/> value), that the proof-of-work gate grants to
    /// the pre-authentication deadline of a connection. Set at most once per connection by the handshake layer;
    /// the transport layer clamps it to 60 seconds.
    /// </summary>
    public static readonly AttributeKey HandshakeDeadlineExtension = AttributeKey.FromName("nalix.connection.handshake_deadline_extension");
}
