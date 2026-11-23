// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using ChatRoom.Contracts;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Security;
using Nalix.Framework.Injection;
using Nalix.Runtime.Extensions;

namespace ChatRoom.Server;

/// <summary>
/// Handles incoming <see cref="ChatMessagePacket"/> messages
/// and broadcasts them to all connected clients.
/// </summary>
[PacketHandler("ChatRoom.Chat")]
public static class ChatHandlers
{
    /// <summary>
    /// Handles a <see cref="ChatMessagePacket"/> by broadcasting it to every
    /// connected client through the <see cref="IConnectionHub"/>.
    /// </summary>
    /// <param name="context">
    /// The packet context carrying the message, connection, and sender.
    /// </param>
    // Handlers require PermissionLevel.USER by default; a connection that has not authenticated
    // is NONE, so this demo lowers the requirement explicitly.
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(0x7101)]
    public static async ValueTask HandleChatAsync(IPacketContext<ChatMessagePacket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Retrieve the broadcaster registered by the Hosting builder.
        // The ConnectionHub implements IConnectionBroadcaster.
        IConnectionBroadcaster? hub = InstanceManager.Instance
            .GetExistingInstance<IConnectionBroadcaster>();

        if (hub is null)
        {
            return;
        }

        // Broadcast the received packet to all connected clients.
        // The BroadcastAsync extension serializes the packet once and
        // applies the per-connection compression/encryption pipeline.
        // Note: This includes the sender. See README for details.
        await hub.BroadcastAsync(context.Packet).ConfigureAwait(false);
    }
}
