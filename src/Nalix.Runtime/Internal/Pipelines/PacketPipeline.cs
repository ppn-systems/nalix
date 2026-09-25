// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Transforms;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;

namespace Nalix.Runtime.Internal.Pipelines;

/// <summary>
/// Shared zero-allocation helpers for serializing and framing packets.
/// Used by both PacketSender (unicast) and ConnectionHubExtensions (broadcast/multicast).
/// </summary>
internal static class PacketPipeline
{
    /// <summary>
    /// Serializes a packet into a pooled BufferLease.
    /// The caller owns the returned lease and must dispose it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static BufferLease Serialize(IPacket packet)
    {
        int packetLength = packet.Length;
        // Reserve transport headroom so the stream transport can prepend its length header in place.
        BufferLease lease = BufferLease.Rent(packetLength, zeroOnDispose: false, BufferLease.TransportHeadroom);
        int written = packet.Serialize(lease.SpanFull);
        lease.CommitLength(written);
        return lease;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static BufferLease CloneWithHeadroom(ReadOnlySpan<byte> source)
    {
        BufferLease lease = BufferLease.Rent(source.Length, zeroOnDispose: false, BufferLease.TransportHeadroom);
        source.CopyTo(lease.SpanFull);
        lease.CommitLength(source.Length);
        return lease;
    }

    /// <summary>
    /// Applies FramePipeline.ProcessOutbound + UDP signing (if applicable) per-connection,
    /// then sends via the specified transport.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static async ValueTask ProcessAndSendAsync(
        IConnection connection, IConnection.ITransport transport, [Borrowed] IBufferLease rawLease,
        bool needEncrypt, bool enableCompress, int minSizeToCompress, CancellationToken ct, bool cloneLease = true)
    {
        // Clone raw lease for per-connection mutation if cloneLease is true
        BufferLease workingLease = cloneLease ? CloneWithHeadroom(rawLease.Span) : (BufferLease)rawLease;

        try
        {
            IBufferLease current = workingLease;

            // Sequence reservation and the wire write must be atomic per-transport: reserving
            // NextSendSequence() before the actual write can complete lets two concurrent senders
            // reserve seq in one order yet hit the wire in the opposite order, causing the
            // receiver's strict monotonic check to drop the lower-seq packet as a false replay.
            // ITransport.AcquireSendLockAsync/SendAsyncCore let each transport define what
            // "atomic with reservation" means: ordered async streams such as TCP/WebSocket
            // lock across reservation and write, while UDP remains datagram/window based.
            await using ConfiguredAsyncDisposable sendScope = (await transport.AcquireSendLockAsync(ct)
                .ConfigureAwait(false)).ConfigureAwait(false);

            if (needEncrypt && transport.SendSequence.IsApproachingOverflow())
            {
                // Pre-emptively disconnect before the counter can wrap and force nonce reuse.
                // Existing key-rotation (RekeyExtensions.RekeyAsync) resets counters on the client
                // side; a clean disconnect here is the server-side equivalent when no rekey has
                // happened in time.
                connection.Disconnect("Send sequence counter approaching overflow; reconnect required.");
                return;
            }

            uint? sequenceToUse = needEncrypt ? transport.NextSendSequence() : null;

            // FramePipeline mutates `current` and properly cleans up older leases.
            FramePipeline.ProcessOutbound(
                ref current,
                enableCompress,
                minSizeToCompress,
                needEncrypt,
                connection.Secret.AsSpan(),
                sequenceToUse,
                connection.Algorithm);

            try
            {
                if (transport == connection.UDP)
                {
                    int dataLen = current.Length;
                    BufferLease signedLease = BufferLease.Rent(dataLen + Math.Max(4, Bytes32.Size));
                    try
                    {
                        current.Span.CopyTo(signedLease.SpanFull);
                        connection.Secret.AsSpan().CopyTo(signedLease.SpanFull[dataLen..]);
                        uint hash = XxHash32.Compute(signedLease.SpanFull[..(dataLen + Bytes32.Size)]);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(signedLease.SpanFull.Slice(dataLen, 4), hash);

                        // [SECURITY] Scrub the rest of the secret: it sits past the committed length and
                        // the pool does not clear arrays on return.
                        signedLease.SpanFull.Slice(dataLen + 4, Bytes32.Size - 4).Clear();
                        signedLease.CommitLength(dataLen + 4);

                        await transport.SendAsyncCore(signedLease.Memory, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        signedLease.Dispose();
                    }
                }
                else
                {
                    await transport.SendAsyncCore(current, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                // Only dispose `current` if it was replaced.
                // `workingLease` itself will be disposed in the outer finally or by the caller.
                if (current != workingLease)
                {
                    current.Dispose();
                }
            }
        }
        finally
        {
            if (cloneLease)
            {
                workingLease.Dispose();
            }
        }
    }
}
