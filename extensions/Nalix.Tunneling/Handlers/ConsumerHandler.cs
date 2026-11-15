// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Logging;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using Nalix.Runtime.Extensions;

namespace Nalix.Tunneling.Handlers;

/// <summary>
/// Handles the TunnelConnect packet from the Consumer.
/// </summary>
[PacketHandler("Nalix.Tunneling")]
public sealed class ConsumerHandler
{
    private readonly ILogger _logger;
    private readonly TunnelOptions _options;
    private readonly TunnelRegistry _tunnelRegistry;
    private readonly ProviderRegistry _providerRegistry;
    private readonly TunnelSessionRegistry _sessionRegistry;

    public ConsumerHandler(ProviderRegistry providerRegistry, TunnelRegistry tunnelRegistry, TunnelSessionRegistry sessionRegistry, TunnelOptions options, ILogger logger)
    {
        _logger = logger;
        _options = options;
        _tunnelRegistry = tunnelRegistry;
        _sessionRegistry = sessionRegistry;
        _providerRegistry = providerRegistry;
    }

    [PacketPermission(PermissionLevel.USER)]
    [PacketOpcode(ProtocolOpCode.TUNNEL_CONNECT)]
    public async ValueTask HandleConnectAsync(IPacketContext<TunnelConnect> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ushort channelId = context.Packet.ChannelId;
        IConnection consumerConnection = context.Connection;

        // One active tunnel per consumer connection.
        if (_sessionRegistry.Contains(consumerConnection.ConnectionId))
        {
            await SendAsync(context, false, 4).ConfigureAwait(false); // 4 = AlreadyTunneling
            return;
        }

        // 1. Find the Provider for this channel
        if (!_providerRegistry.TryGetProvider(channelId, out IConnection? providerControlConnection))
        {
            if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[Tunneling.ConsumerHandler] consumer={ConnectionId} channel={ChannelId} error=no-provider-registered", consumerConnection.ConnectionId, channelId);
            }

            await SendAsync(context, false, 1).ConfigureAwait(false); // 1 = ChannelNotFound
            return;
        }

        // Bound both established sessions and pending requests (each pending request forwards work to the provider).
        if (_sessionRegistry.Count + _tunnelRegistry.PendingCount >= _options.MaxConcurrentTunnels)
        {
            if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[Tunneling.ConsumerHandler] consumer={ConnectionId} channel={ChannelId} error=tunnel-limit-reached", consumerConnection.ConnectionId, channelId);
            }

            await SendAsync(context, false, 3).ConfigureAwait(false); // 3 = TunnelLimitReached
            return;
        }

        // 2. Register a pending tunnel to get a token
        (Task<IConnection>? providerTask, Abstractions.Primitives.Bytes32 token) = _tunnelRegistry.Register();

        // 3. Request the Provider to open a new data connection
        using (PacketScope<TunnelRequest> requestScope = PacketFactory<TunnelRequest>.Acquire())
        {
            TunnelRequest request = requestScope.Value;
            request.Token = token;

            // Forward the request to the provider. The token is a bearer secret, so it is always encrypted:
            // the sender's metadata comes from TUNNEL_PROVIDE and does not request encryption. Without a
            // negotiated cipher the send throws instead of falling back to plaintext.
            await providerControlConnection.SendAsync(request, NetworkTransport.TCP, enableEncrypt: true).ConfigureAwait(false);
        }

        if (_logger != null && _logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "[Tunneling.ConsumerHandler] requested-data-connection provider={ProviderId} consumer={ConsumerId} channel={ChannelId}",
                providerControlConnection.ConnectionId, consumerConnection.ConnectionId, channelId);
        }

        // 4. Wait for the Provider to establish the new data connection
        IConnection? providerDataConnection;
        try
        {
            // Timeout to prevent hanging tasks if provider ignores the request
            providerDataConnection = await providerTask.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[Tunneling.ConsumerHandler] data-connection-timeout provider={ProviderId} channel={ChannelId}", providerControlConnection.ConnectionId, channelId);
            }

            // Drop the pending entry so a late TUNNEL_READY cannot resolve it. If it already resolved, close the connection nobody will use.
            if (!_tunnelRegistry.Remove(token))
            {
                DisconnectWhenResolved(providerTask);
            }

            await SendAsync(context, false, 2).ConfigureAwait(false); // 2 = ProviderTimeout
            return;
        }

        // 5. Send success ACK to consumer
        await SendAsync(context, true, 0).ConfigureAwait(false);

        // 6. Create TunnelSession and start piping
        TunnelSession session = new(consumerConnection, _options, _sessionRegistry, _logger);
        try
        {
            _sessionRegistry.Register(consumerConnection.ConnectionId, session);
            session.StartTunnel(providerDataConnection);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[Tunneling.ConsumerHandler] tunnel-session-start-error consumer={ConsumerId}", consumerConnection.ConnectionId);
            }

            providerDataConnection.Disconnect("tunnel-session-start-failed");
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void DisconnectWhenResolved(Task<IConnection> providerTask) => _ = providerTask.ContinueWith(
        static t => t.Result.Disconnect("tunnel-request-timed-out"),
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnRanToCompletion,
        TaskScheduler.Default);

    private static async ValueTask SendAsync(IPacketContext<TunnelConnect> context, bool success, byte reason)
    {
        using PacketScope<TunnelConnectAck> scope = PacketFactory<TunnelConnectAck>.Acquire();
        TunnelConnectAck ack = scope.Value;
        ack.Success = success;
        ack.Reason = reason;

        await context.Sender.SendAsync(ack).ConfigureAwait(false);
    }
}

