// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Framework.Injection;
using Nalix.Runtime.Extensions;

namespace Nalix.Runtime.Handlers;

/// <summary>
/// Issues datagram session tickets to established stream connections (NDP bootstrap, §4 of the design).
/// </summary>
[PacketHandler("Nalix.Datagram")]
public static partial class DatagramTicketHandlers
{
    /// <summary>
    /// Answers a <see cref="DatagramTicketStage.REQUEST"/> with a ticket from the registered
    /// <see cref="IDatagramTicketIssuer"/>, or with a refusal reason.
    /// </summary>
    /// <param name="context">The packet context.</param>
    /// <returns>A task that completes when the answer was sent.</returns>
    [ReservedOpcodePermitted]
    [PacketEncryption(false)]
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.DATAGRAM_TICKET)]
    public static async ValueTask HandleAsync(IPacketContext<DatagramTicket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DatagramTicket packet = context.Packet;
        if (!packet.Validate(out _) || packet.Stage != DatagramTicketStage.REQUEST)
        {
            return;
        }

        IConnection connection = context.Connection;
        ProtocolReason refusal = ProtocolReason.NONE;
        DatagramTicketInfo ticket = default;

        if (!connection.GetRuntimeState().HandshakeEstablished || connection.Secret.IsZero)
        {
            refusal = ProtocolReason.UNAUTHENTICATED;
        }
        else if (connection.Transport is IDatagramTransport)
        {
            refusal = ProtocolReason.STATE_VIOLATION;
        }
        else if (InstanceManager.Instance.GetExistingInstance<IDatagramTicketIssuer>() is not { } issuer)
        {
            refusal = ProtocolReason.SERVICE_UNAVAILABLE;
        }
        else
        {
            try
            {
                // The datagram connection inherits the completed handshake of the stream connection.
                ticket = issuer.IssueTicket(connection, static datagram => datagram.GetRuntimeState().HandshakeEstablished = true);
            }
            catch (InvalidOperationException)
            {
                refusal = ProtocolReason.SERVICE_UNAVAILABLE;
            }
        }

        using PacketScope<DatagramTicket> lease = PacketFactory<DatagramTicket>.Acquire();
        DatagramTicket response = lease.Value;
        if (refusal == ProtocolReason.NONE)
        {
            response.InitializeResponse(ticket);
        }
        else
        {
            response.InitializeRefusal(refusal);
        }

        await context.Sender.SendAsync(response).ConfigureAwait(false);
    }
}
