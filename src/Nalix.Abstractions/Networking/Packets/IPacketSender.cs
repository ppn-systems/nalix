// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nalix.Abstractions.Networking.Packets;

/// <summary>
/// Abstracts packet sending with automatic transform (encrypt/compress)
/// </summary>
public interface IPacketSender : IPoolable
{
    /// <inheritdoc/>
    void Initialize<TPacket>(IPacketContext<TPacket> context) where TPacket : IPacket;

    /// <summary>
    /// Sends a packet, applying encryption/compression automatically
    /// based on the metadata of the current handler.
    /// </summary>
    /// <param name="packet">
    /// The packet instance to send.
    /// </param>
    /// <param name="ct">
    /// A cancellation token that can cancel the send operation.
    /// </param>
    ValueTask SendAsync(IPacket packet, CancellationToken ct = default);

    /// <summary>
    /// Sends a packet, explicitly overriding the encryption flag.
    /// </summary>
    /// <param name="packet">
    /// The packet instance to send.
    /// </param>
    /// <param name="forceEncrypt">
    /// <c>true</c> to force encryption even if metadata would not require it.
    /// </param>
    /// <param name="ct">
    /// A cancellation token that can cancel the send operation.
    /// </param>
    ValueTask SendAsync(IPacket packet, bool forceEncrypt, CancellationToken ct = default);

    /// <summary>
    /// Sends <paramref name="response"/> as a reply to the packet currently being processed,
    /// automatically echoing its <c>SequenceId</c> so the client's request/response and
    /// stream correlation logic can match the reply. Prefer this over <see cref="SendAsync(IPacket, CancellationToken)"/>
    /// whenever the outgoing packet is a direct response to the handler's inbound packet.
    /// </summary>
    /// <param name="response">
    /// The response packet instance to send.
    /// </param>
    /// <param name="ct">
    /// A cancellation token that can cancel the send operation.
    /// </param>
    ValueTask ReplyAsync(IPacket response, CancellationToken ct = default);

    /// <summary>
    /// Sends a batch of packets to the same connection, in order, applying encryption/compression
    /// automatically based on the metadata of the current handler. Each packet is still its own frame
    /// on the wire; an implementation may hold the send lock once for the whole batch and coalesce the
    /// frames into fewer transport writes.
    /// </summary>
    /// <typeparam name="TPacket">The packet type.</typeparam>
    /// <param name="packets">
    /// The packets to send. They are read before this method returns, so the caller may release them
    /// once the returned task completes.
    /// </param>
    /// <param name="ct">
    /// A cancellation token that can cancel the send operation.
    /// </param>
    ValueTask SendAsync<TPacket>(ReadOnlySpan<TPacket> packets, CancellationToken ct = default) where TPacket : IPacket
    {
        if (packets.IsEmpty)
        {
            return default;
        }

        IPacket[] copy = new IPacket[packets.Length];
        for (int i = 0; i < packets.Length; i++)
        {
            copy[i] = packets[i];
        }

        return SEND_SEQUENTIAL_ASYNC(this, copy, ct);

        static async ValueTask SEND_SEQUENTIAL_ASYNC(IPacketSender sender, IPacket[] packets, CancellationToken ct)
        {
            for (int i = 0; i < packets.Length; i++)
            {
                await sender.SendAsync(packets[i], ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends a batch of packets as replies to the packet currently being processed: every packet gets
    /// the request's <c>SequenceId</c> (see <see cref="ReplyAsync(IPacket, CancellationToken)"/>) and the
    /// batch is then sent like <see cref="SendAsync{TPacket}(ReadOnlySpan{TPacket}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TPacket">The packet type.</typeparam>
    /// <param name="responses">
    /// The response packets to send. They are read before this method returns.
    /// </param>
    /// <param name="ct">
    /// A cancellation token that can cancel the send operation.
    /// </param>
    ValueTask ReplyAsync<TPacket>(ReadOnlySpan<TPacket> responses, CancellationToken ct = default) where TPacket : IPacket
    {
        if (responses.IsEmpty)
        {
            return default;
        }

        IPacket[] copy = new IPacket[responses.Length];
        for (int i = 0; i < responses.Length; i++)
        {
            copy[i] = responses[i];
        }

        return REPLY_SEQUENTIAL_ASYNC(this, copy, ct);

        static async ValueTask REPLY_SEQUENTIAL_ASYNC(IPacketSender sender, IPacket[] responses, CancellationToken ct)
        {
            for (int i = 0; i < responses.Length; i++)
            {
                await sender.ReplyAsync(responses[i], ct).ConfigureAwait(false);
            }
        }
    }
}
