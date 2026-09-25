// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using BlazorWasm.Contracts;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.Pooling;
using Nalix.Framework.Injection;
using Nalix.Runtime.Extensions;

namespace BlazorWasm.Server;

/// <summary>
/// Packet handlers used by the Blazor WebAssembly sample.
/// </summary>
[PacketHandler("BlazorWasm.Demo")]
public static class DemoHandlers
{
    /// <summary>
    /// Request/response: replies to the caller only.
    /// </summary>
    [PacketOpcode(0x7301)]
    public static async ValueTask HandleEchoAsync(IPacketContext<EchoRequestPacket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using PacketScope<EchoResponsePacket> lease = PacketFactory<EchoResponsePacket>.Acquire();
        EchoResponsePacket response = lease.Value;
        response.Text = context.Packet.Text.ToUpperInvariant();
        response.ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // The SDK's RequestAsync<T> on the client resolves with this packet.
        await context.Sender.SendAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Server push: re-broadcasts a chat line to every connected client (including the sender).
    /// </summary>
    [PacketOpcode(0x7303)]
    public static async ValueTask HandleChatAsync(IPacketContext<ChatMessagePacket> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IConnectionBroadcaster? hub = InstanceManager.Instance.GetExistingInstance<IConnectionBroadcaster>();
        if (hub is null)
        {
            return;
        }

        // BroadcastAsync serializes once and applies each connection's own encryption.
        await hub.BroadcastAsync(context.Packet).ConfigureAwait(false);
    }
}
