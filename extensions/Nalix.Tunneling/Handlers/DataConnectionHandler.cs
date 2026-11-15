// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Logging;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;

namespace Nalix.Tunneling.Handlers;

/// <summary>
/// Handles the TunnelReady packet from the Provider's Data Connection.
/// </summary>
[PacketHandler("Nalix.Tunneling")]
public sealed class DataConnectionHandler
{
    private readonly ILogger _logger;
    private readonly TunnelRegistry _registry;

    public DataConnectionHandler(TunnelRegistry registry, ILogger logger)
    {
        _logger = logger;
        _registry = registry;
    }

    // NONE: the data connection is a fresh, unauthenticated TCP connection. The 256-bit one-time token
    // resolved below is the only authorization.
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.TUNNEL_READY)]
    public ValueTask HandleReadyAsync(IPacketContext<TunnelReady> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The connection sending this packet is the NEW Data Connection from the Provider
        IConnection dataConnection = context.Connection;
        Bytes32 token = context.Packet.Token;

        bool resolved = _registry.Resolve(token, dataConnection);

        if (resolved)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[Tunneling.DataConnectionHandler] authenticated-and-resolved connection={ConnectionId}", dataConnection.ConnectionId);
            }
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[Tunneling.DataConnectionHandler] authentication-failed connection={ConnectionId} reason=invalid-token", dataConnection.ConnectionId);
            }

            dataConnection.Disconnect("Invalid tunnel token");
        }

        return ValueTask.CompletedTask;
    }
}

