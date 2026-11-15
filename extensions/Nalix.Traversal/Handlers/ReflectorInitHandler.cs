// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using Nalix.Runtime.Extensions;
using Nalix.Traversal.Packets;
using Nalix.Traversal.Reflector;

namespace Nalix.Traversal.Handlers;

/// <summary>
/// Handles ReflectorInits from clients to establish a new Reflector Session.
/// </summary>
[PacketHandler("Nalix.Traversal")]
public sealed class ReflectorInitHandler
{
    private readonly ReflectorManager _manager;

    public ReflectorInitHandler(ReflectorManager reflectorManager) => _manager = reflectorManager;

    /// <summary>
    /// Handles the Reflector request.
    /// </summary>
    [PacketPermission(PermissionLevel.USER)]
    [PacketOpcode(ProtocolOpCode.TRAVERSAL_REFLECTOR_INIT)]
    public async ValueTask HandleAsync(IPacketContext<ReflectorInit> context)
    {
        System.ArgumentNullException.ThrowIfNull(context);

        ulong peerAId = context.Connection.ConnectionId;
        ulong peerBId = context.Packet.TargetPeerId;

        // 2. Create Reflector Session (Bind lifecycle to requester's connection)
        ulong token = _manager.CreateSession(peerAId, peerBId, context.Connection);

        // 3. Send Response
        using PacketScope<ReflectorAllocated> scope = PacketFactory<ReflectorAllocated>.Acquire();

        ReflectorAllocated response = scope.Value;

        response.ReflectorToken = token;
        response.Success = true;
        response.PeerId = peerBId;
        response.RequestNonce = context.Packet.RequestNonce;
        response.SequenceId = context.Packet.SequenceId;

        await context.Sender.SendAsync(response).ConfigureAwait(false);

        // 4. Notify PeerB (TargetPeerId) so they also know the ReflectorToken
        IConnectionHub? hub = context.Connection.GetHub();
        if (hub != null)
        {
            IConnection? targetConnection = hub.GetConnection(peerBId);
            if (targetConnection != null)
            {
                using PacketScope<ReflectorAllocated> peerBScope = PacketFactory<ReflectorAllocated>.Acquire();
                ReflectorAllocated peerBNotification = peerBScope.Value;

                peerBNotification.ReflectorToken = token;
                peerBNotification.Success = true;
                peerBNotification.PeerId = peerAId;
                peerBNotification.RequestNonce = 0; // Server-initiated message, not an answer to a request
                peerBNotification.SequenceId = 0; // Server-initiated message, no request sequence to match

                await targetConnection.SendAsync(
                    peerBNotification,
                    NetworkTransport.TCP,
                    enableEncrypt: true,
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
    }
}

