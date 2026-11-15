// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.Tunneling;

/// <summary>
/// Manages active Tunnel Providers.
/// Maps ChannelId to the Provider's Control Connection.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<ushort, (IConnection Connection, IPacketSender Sender)> _providers = new();

    /// <summary>
    /// Registers a provider for a specific channel.
    /// </summary>
    /// <param name="channelId">The channel ID to register.</param>
    /// <param name="connection">The control connection of the provider.</param>
    /// <param name="sender">The packet sender for the provider.</param>
    /// <returns>True if registration is successful; false if the channel is already taken.</returns>
    public bool TryRegister(ushort channelId, IConnection connection, IPacketSender sender)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sender);

        if (_providers.TryAdd(channelId, (connection, sender)))
        {
            // Bind lifecycle: release the channel when its owning connection closes
            connection.ConnectionClosed += (s, e) => _ = this.TryUnregister(channelId, connection);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Releases a channel, but only when it is currently owned by <paramref name="connection"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the channel was released; otherwise <see langword="false"/>.</returns>
    internal bool TryUnregister(ushort channelId, IConnection connection)
    {
        if (_providers.TryGetValue(channelId, out (IConnection Connection, IPacketSender Sender) existing) &&
            existing.Connection == connection)
        {
            // Remove by key and value so a concurrent re-registration by another connection is never removed.
            return _providers.TryRemove(new KeyValuePair<ushort, (IConnection Connection, IPacketSender Sender)>(channelId, existing));
        }

        return false;
    }

    /// <summary>
    /// Attempts to retrieve the provider's connection and sender for the given channel.
    /// </summary>
    public bool TryGetProvider(ushort channelId, [NotNullWhen(true)] out IConnection? connection, [NotNullWhen(true)] out IPacketSender? sender)
    {
        if (_providers.TryGetValue(channelId, out (IConnection Connection, IPacketSender Sender) tuple))
        {
            connection = tuple.Connection;
            sender = tuple.Sender;
            return true;
        }

        connection = null;
        sender = null;
        return false;
    }
}
