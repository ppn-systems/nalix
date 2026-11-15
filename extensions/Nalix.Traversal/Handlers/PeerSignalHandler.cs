// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using Nalix.Runtime.Extensions;
using Nalix.Traversal.Packets;

namespace Nalix.Traversal.Handlers;

/// <summary>
/// Handles peer signaling for NAT traversal.
/// Forwards STUN discovered IP/Port information between peers over TCP.
/// <para>
/// A <see cref="SignalType.CandidateOffer"/> is relayed only when its address is the sender's own
/// server-observed IP (the port is the sender's UDP mapping and cannot be checked). Address fields on a
/// <see cref="SignalType.Request"/> are not relayed. Peers therefore cannot direct a third party's UDP
/// probes at an arbitrary address.
/// </para>
/// </summary>
[PacketHandler("Nalix.Traversal")]
public sealed class PeerSignalHandler
{
    /// <summary>
    /// Processes the incoming <see cref="PeerSignal"/>.
    /// </summary>
    [PacketEncryption(true)]
    [PacketPermission(PermissionLevel.USER)]
    [PacketOpcode(ProtocolOpCode.TRAVERSAL_PEER_SIGNAL)]
    public static async ValueTask HandleAsync(IPacketContext<PeerSignal> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        SignalType type = context.Packet.Type;

        // Server only brokers Request and CandidateOffer messages.
        if (type != SignalType.Request && type != SignalType.CandidateOffer)
        {
            return;
        }

        ulong senderId = context.Connection.ConnectionId;
        ulong targetId = context.Packet.TargetPeerId;

        IConnectionHub? hub = context.Connection.GetHub();
        IConnection? targetConnection = hub?.GetConnection(targetId);

        if (targetConnection != null && IsRelayable(context.Connection, context.Packet, type))
        {
            using PacketScope<PeerSignal> lease = PacketFactory<PeerSignal>.Acquire();
            PeerSignal forwardedPacket = lease.Value;

            forwardedPacket.TargetPeerId = senderId;
            forwardedPacket.Type = SignalType.CandidateOffer;

            forwardedPacket.SequenceId = 0;

            if (type == SignalType.CandidateOffer)
            {
                forwardedPacket.Port = context.Packet.Port;
                forwardedPacket.AddressLow = context.Packet.AddressLow;
                forwardedPacket.AddressHigh = context.Packet.AddressHigh;
            }

            await targetConnection.SendAsync(forwardedPacket, NetworkTransport.TCP, enableEncrypt: true, context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            using PacketScope<PeerSignal> lease = PacketFactory<PeerSignal>.Acquire();
            PeerSignal errorPacket = lease.Value;

            errorPacket.Type = SignalType.Result;
            errorPacket.TargetPeerId = targetId;
            errorPacket.SequenceId = context.Packet.SequenceId;

            await context.Sender.SendAsync(errorPacket, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRelayable(IConnection sender, PeerSignal packet, SignalType type)
    {
        if (type == SignalType.Request)
        {
            return true;
        }

        if (packet.Port == 0 || !IPAddress.TryParse(sender.NetworkEndpoint.Address, out IPAddress? observed))
        {
            return false;
        }

        // Candidate bytes: Low then High, little-endian (the layout used by TraversalClient). Compare in
        // IPv4-mapped IPv6 form so an IPv4 sender matches either encoding.
        Span<byte> claimed = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(claimed, packet.AddressLow);
        BinaryPrimitives.WriteUInt64LittleEndian(claimed[8..], packet.AddressHigh);

        Span<byte> actual = stackalloc byte[16];
        return observed.MapToIPv6().TryWriteBytes(actual, out _) && claimed.SequenceEqual(actual);
    }

}

