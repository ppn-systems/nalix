// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Logging;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;

namespace Nalix.Tunneling.Handlers;

/// <summary>
/// Handles the TunnelProvide packet from the Provider.
/// </summary>
[PacketHandler("Nalix.Tunneling")]
public sealed class ProviderHandler
{
    private readonly ILogger? _logger;
    private readonly ProviderRegistry _registry;

    public ProviderHandler(ProviderRegistry registry, ILogger? logger)
    {
        _logger = logger;
        _registry = registry;
    }

    [PacketPermission(PermissionLevel.USER)]
    [PacketOpcode(ProtocolOpCode.TUNNEL_PROVIDE)]
    public async ValueTask HandleProvideAsync(IPacketContext<TunnelProvide> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ushort channelId = context.Packet.ChannelId;
        bool success = _registry.TryRegister(channelId, context.Connection, context.Sender);

        if (success)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("[Tunneling.ProviderHandler] provider={ConnectionId} registered channel={ChannelId}", context.Connection.ConnectionId, channelId);
            }
        }
        else
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("[Tunneling.ProviderHandler] provider={ConnectionId} failed-to-register channel={ChannelId} reason=channel-in-use", context.Connection.ConnectionId, channelId);
            }
        }

        using PacketScope<TunnelProvideAck> scope = PacketFactory<TunnelProvideAck>.Acquire();
        TunnelProvideAck ack = scope.Value;
        ack.Success = success;
        ack.Reason = (byte)(success ? 0 : 1); // 1 = Channel already in use

        await context.Sender.SendAsync(ack).ConfigureAwait(false);
    }
}

