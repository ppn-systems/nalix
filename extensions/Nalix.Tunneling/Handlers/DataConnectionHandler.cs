// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Logging;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Pooling;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Primitives;
using Nalix.Environment.Random;

namespace Nalix.Tunneling.Handlers;

/// <summary>
/// Authenticates the Provider's Data Connection in two steps, so the one-time token never crosses the wire:
/// <list type="number">
/// <item><description><c>TUNNEL_READY</c> carries the public tunnel identifier <c>Keccak256(token)</c>. The server
/// replies with <c>TUNNEL_CHALLENGE</c> carrying a fresh nonce for this connection only.</description></item>
/// <item><description><c>TUNNEL_PROOF</c> carries <c>HMAC-Keccak256(key: token, message: nonce)</c>. The server
/// recomputes it from the pending token and resolves the consumer's tunnel only if it matches.</description></item>
/// </list>
/// An observer who sees the identifier, the nonce or the proof cannot replay them on another connection.
/// </summary>
[PacketHandler("Nalix.Tunneling")]
public sealed class DataConnectionHandler
{
    private static readonly AttributeKey ChallengeKey = AttributeKey.FromName("nalix.tunnel.data_challenge");

    private readonly ILogger _logger;
    private readonly TunnelRegistry _registry;

    public DataConnectionHandler(TunnelRegistry registry, ILogger logger)
    {
        _logger = logger;
        _registry = registry;
    }

    // NONE: the data connection is a fresh, unauthenticated TCP connection. Authorization is the
    // token proof completed in HandleProofAsync.
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.TUNNEL_READY)]
    public async ValueTask HandleReadyAsync(IPacketContext<TunnelReady> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The connection sending this packet is the NEW Data Connection from the Provider
        IConnection dataConnection = context.Connection;
        Bytes32 tunnelId = context.Packet.Token;

        if (dataConnection.Attributes.ContainsKey(ChallengeKey) || !_registry.TryGetToken(tunnelId, out _))
        {
            this.Reject(dataConnection, "Invalid tunnel token");
            return;
        }

        Span<byte> nonceBytes = stackalloc byte[Bytes32.Size];
        Csprng.Fill(nonceBytes);
        Bytes32 nonce = new(nonceBytes);
        dataConnection.Attributes[ChallengeKey] = new DataChallenge(tunnelId, nonce);

        using PacketScope<TunnelChallenge> scope = PacketFactory<TunnelChallenge>.Acquire();
        scope.Value.Nonce = nonce;
        await context.Sender.SendAsync(scope.Value).ConfigureAwait(false);
    }

    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.TUNNEL_PROOF)]
    public ValueTask HandleProofAsync(IPacketContext<TunnelProof> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IConnection dataConnection = context.Connection;

        if (!dataConnection.Attributes.TryGetValue(ChallengeKey, out object? state) || state is not DataChallenge challenge)
        {
            this.Reject(dataConnection, "Unexpected tunnel proof");
            return ValueTask.CompletedTask;
        }

        // A challenge answers exactly one proof.
        _ = dataConnection.Attributes.Remove(ChallengeKey);

        if (!_registry.TryGetToken(challenge.TunnelId, out Bytes32 token))
        {
            this.Reject(dataConnection, "Invalid tunnel token");
            return ValueTask.CompletedTask;
        }

        Span<byte> expected = stackalloc byte[Bytes32.Size];
        HmacKeccak256.Compute(token.AsSpan(), challenge.Nonce.AsSpan(), expected);

        if (!BitwiseOperations.FixedTimeEquals(expected, context.Packet.Mac.AsSpan()))
        {
            this.Reject(dataConnection, "Invalid tunnel proof");
            return ValueTask.CompletedTask;
        }

        if (!_registry.Resolve(challenge.TunnelId, dataConnection))
        {
            this.Reject(dataConnection, "Invalid tunnel token");
            return ValueTask.CompletedTask;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[Tunneling.DataConnectionHandler] authenticated-and-resolved connection={ConnectionId}", dataConnection.ConnectionId);
        }

        return ValueTask.CompletedTask;
    }

    private void Reject(IConnection dataConnection, string reason)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("[Tunneling.DataConnectionHandler] authentication-failed connection={ConnectionId} reason={Reason}", dataConnection.ConnectionId, reason);
        }

        dataConnection.Disconnect(reason);
    }

    private sealed class DataChallenge(Bytes32 tunnelId, Bytes32 nonce)
    {
        public Bytes32 TunnelId { get; } = tunnelId;
        public Bytes32 Nonce { get; } = nonce;
    }
}
