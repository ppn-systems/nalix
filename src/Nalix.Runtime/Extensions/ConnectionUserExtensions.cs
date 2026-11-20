// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.Runtime.Extensions;

/// <summary>
/// Provides extension methods for managing User ID mappings and routing messages to specific users.
/// These methods leverage the underlying connection group registry to provide zero-allocation,
/// scalable user routing (e.g., across a Redis backplane).
/// </summary>
public static class ConnectionUserExtensions
{
    private const string UserGroupPrefix = "__user_:";

    /// <summary>
    /// Gets the internal group name used for routing messages to a specific user.
    /// </summary>
    private static string GetUserGroupName(string userId) => string.Concat(UserGroupPrefix, userId);

    /// <summary>
    /// Maps a connection to a specific user identifier.
    /// This allows routing messages to all connections owned by this user.
    /// </summary>
    /// <param name="registry">The connection group registry.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="connection">The connection to map.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task MapToUserAsync(this IConnectionGroupRegistry registry, string userId, IConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(connection);

        // Optional: Cache the UserId on the connection for fast local access
        connection.UserId = userId;

        string groupName = GetUserGroupName(userId);
        return registry.AddToGroupAsync(groupName, connection, cancellationToken);
    }

    /// <summary>
    /// Unmaps a connection from a specific user identifier.
    /// </summary>
    /// <param name="registry">The connection group registry.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="connection">The connection to unmap.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task UnmapUserAsync(this IConnectionGroupRegistry registry, string userId, IConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.UserId == userId)
        {
            connection.UserId = null;
        }

        string groupName = GetUserGroupName(userId);
        return registry.RemoveFromGroupAsync(groupName, connection, cancellationToken);
    }

    /// <summary>
    /// Sends a packet to all connections mapped to a specific user identifier.
    /// The packet payload is automatically compressed (if enabled and large enough) and encrypted per connection.
    /// </summary>
    /// <param name="hub">The connection broadcaster.</param>
    /// <param name="registry">The connection group registry used for resolving user connections.</param>
    /// <param name="userId">The target user identifier.</param>
    /// <param name="packet">The packet to send.</param>
    /// <param name="enableEncrypt">Whether to encrypt the packet (defaults to true).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous multicast operation.</returns>
    public static Task SendToUserAsync(
        this IConnectionBroadcaster hub,
        IConnectionGroupRegistry registry,
        string userId,
        IPacket packet,
        bool enableEncrypt = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(packet);

        string groupName = GetUserGroupName(userId);
        return hub.MulticastAsync(registry, groupName, packet, enableEncrypt, cancellationToken);
    }

    /// <summary>
    /// Sends a packet to all connections mapped to a specific user identifier, excluding a specific connection.
    /// </summary>
    /// <param name="hub">The connection broadcaster.</param>
    /// <param name="registry">The connection group registry used for resolving user connections.</param>
    /// <param name="userId">The target user identifier.</param>
    /// <param name="excludedConnection">The connection to exclude from the broadcast.</param>
    /// <param name="packet">The packet to send.</param>
    /// <param name="enableEncrypt">Whether to encrypt the packet (defaults to true).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous multicast operation.</returns>
    public static Task SendToUserExceptAsync(
        this IConnectionBroadcaster hub,
        IConnectionGroupRegistry registry,
        string userId,
        IConnection excludedConnection,
        IPacket packet,
        bool enableEncrypt = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(excludedConnection);
        ArgumentNullException.ThrowIfNull(packet);

        string groupName = GetUserGroupName(userId);
        return hub.MulticastExceptAsync(registry, groupName, excludedConnection, packet, enableEncrypt, cancellationToken);
    }
}
