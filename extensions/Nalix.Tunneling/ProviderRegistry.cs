// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nalix.Abstractions.Networking;

namespace Nalix.Tunneling;

/// <summary>
/// Manages active Tunnel Providers.
/// Maps ChannelId to the Provider's Control Connection.
/// </summary>
public sealed class ProviderRegistry
{
    // Only the connection is kept. A PacketSender belongs to a pooled PacketContext and is reset when its
    // handler returns, so it must not outlive the dispatch that created it.
    private readonly ConcurrentDictionary<ushort, IConnection> _providers = new();

    /// <summary>
    /// Registers a provider for a specific channel.
    /// </summary>
    /// <param name="channelId">The channel ID to register.</param>
    /// <param name="connection">The control connection of the provider.</param>
    /// <returns>True if registration is successful; false if the channel is already taken.</returns>
    public bool TryRegister(ushort channelId, IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (_providers.TryAdd(channelId, connection))
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
        if (_providers.TryGetValue(channelId, out IConnection? existing) && existing == connection)
        {
            // Remove by key and value so a concurrent re-registration by another connection is never removed.
            return _providers.TryRemove(new KeyValuePair<ushort, IConnection>(channelId, existing));
        }

        return false;
    }

    /// <summary>
    /// Attempts to retrieve the provider's control connection for the given channel.
    /// </summary>
    public bool TryGetProvider(ushort channelId, [NotNullWhen(true)] out IConnection? connection)
    {
        if (_providers.TryGetValue(channelId, out IConnection? tuple))
        {
            connection = tuple;
            return true;
        }

        connection = null;
        return false;
    }
}
