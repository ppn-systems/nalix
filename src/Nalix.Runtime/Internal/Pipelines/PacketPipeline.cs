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
    internal static BufferLease Serialize(IPacket packet, bool zeroOnDispose = false)
    {
        int packetLength = packet.Length;
        // Reserve transport headroom so the stream transport can prepend its length header in place.
        // [SECURITY] Callers pass zeroOnDispose when the frame will be encrypted: the pool does not
        // clear arrays on return, so the plaintext must be scrubbed by its owner even if the send
        // bails out before FramePipeline.ProcessOutbound runs (cancellation, sequence overflow).
        BufferLease lease = BufferLease.Rent(packetLength, zeroOnDispose, BufferLease.TransportHeadroom);
        int written = packet.Serialize(lease.SpanFull);
        lease.CommitLength(written);
        return lease;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static BufferLease CloneWithHeadroom(ReadOnlySpan<byte> source, bool zeroOnDispose)
    {
        BufferLease lease = BufferLease.Rent(source.Length, zeroOnDispose, BufferLease.TransportHeadroom);
        source.CopyTo(lease.SpanFull);
        lease.CommitLength(source.Length);
        return lease;
    }

    /// <summary>
    /// Applies FramePipeline.ProcessOutbound + UDP signing (if applicable) per-connection,
    /// then sends via the specified transport.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ValueTask ProcessAndSendAsync(
        IConnection connection, IConnection.ITransport transport, [Borrowed] IBufferLease rawLease,
        bool needEncrypt, bool enableCompress, int minSizeToCompress, CancellationToken ct, bool cloneLease = true)
    {
        ValueTask<IAsyncDisposable> lockVt = transport.AcquireSendLockAsync(ct);
        if (lockVt.IsCompletedSuccessfully)
        {
            return EXECUTE_WITH_LOCK(connection, transport, rawLease, lockVt.Result, needEncrypt, enableCompress, minSizeToCompress, ct, cloneLease);
        }

        return AWAIT_LOCK_AND_SEND_ASYNC(lockVt, connection, transport, rawLease, needEncrypt, enableCompress, minSizeToCompress, ct, cloneLease);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask EXECUTE_WITH_LOCK(
        IConnection connection, IConnection.ITransport transport, [Borrowed] IBufferLease rawLease,
        IAsyncDisposable sendScope, bool needEncrypt, bool enableCompress, int minSizeToCompress, CancellationToken ct, bool cloneLease)
    {
        BufferLease workingLease = cloneLease ? CloneWithHeadroom(rawLease.Span, needEncrypt) : (BufferLease)rawLease;
        IBufferLease current = workingLease;
        bool completed = false;

        try
        {
            if (needEncrypt && transport.SendSequence.IsApproachingOverflow())
            {
                connection.Disconnect("Send sequence counter approaching overflow; reconnect required.");
                completed = true;
                return DISPOSE_AND_RETURN(sendScope, current, workingLease, cloneLease);
            }

            uint? sequenceToUse = needEncrypt ? transport.NextSendSequence() : null;

            FramePipeline.ProcessOutbound(
                ref current,
                enableCompress,
                minSizeToCompress,
                needEncrypt,
                connection.Secret.AsSpan(),
                sequenceToUse,
                connection.Algorithm);

            ValueTask sendVt;
            if (transport == connection.UDP)
            {
                sendVt = SEND_UDP_ASYNC(connection, transport, current, ct);
            }
            else
            {
                sendVt = transport.SendAsyncCore(current, ct);
            }

            if (sendVt.IsCompletedSuccessfully)
            {
                completed = true;
                return DISPOSE_AND_RETURN(sendScope, current, workingLease, cloneLease);
            }

            completed = true;
            return AWAIT_SEND_AND_CLEANUP_ASYNC(sendVt, sendScope, current, workingLease, cloneLease);
        }
        finally
        {
            if (!completed)
            {
                if (current != workingLease)
                {
                    current.Dispose();
                }
                if (cloneLease)
                {
                    workingLease.Dispose();
                }

                ValueTask disp = sendScope.DisposeAsync();
#pragma warning disable CA1849 // Synchronous rollback path
                if (disp.IsCompletedSuccessfully)
                {
                    disp.GetAwaiter().GetResult();
                }
                else
                {
                    disp.AsTask().GetAwaiter().GetResult();
                }
#pragma warning restore CA1849
            }
        }

        static ValueTask DISPOSE_AND_RETURN(IAsyncDisposable scope, IBufferLease cur, BufferLease work, bool clone)
        {
            if (cur != work)
            {
                cur.Dispose();
            }
            if (clone)
            {
                work.Dispose();
            }

            ValueTask disp = scope.DisposeAsync();
            if (disp.IsCompletedSuccessfully)
            {
                return default;
            }

            return AWAIT_DISPOSE_ASYNC(disp);
        }

        static async ValueTask AWAIT_DISPOSE_ASYNC(ValueTask disp) => await disp.ConfigureAwait(false);
    }

    private static async ValueTask AWAIT_LOCK_AND_SEND_ASYNC(
        ValueTask<IAsyncDisposable> lockVt,
        IConnection connection, IConnection.ITransport transport, [Borrowed] IBufferLease rawLease,
        bool needEncrypt, bool enableCompress, int minSizeToCompress, CancellationToken ct, bool cloneLease)
    {
        IAsyncDisposable sendScope = await lockVt.ConfigureAwait(false);
        await EXECUTE_WITH_LOCK(connection, transport, rawLease, sendScope, needEncrypt, enableCompress, minSizeToCompress, ct, cloneLease).ConfigureAwait(false);
    }

    private static async ValueTask AWAIT_SEND_AND_CLEANUP_ASYNC(
        ValueTask sendVt, IAsyncDisposable sendScope, IBufferLease current, BufferLease workingLease, bool cloneLease)
    {
        try
        {
            await sendVt.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await sendScope.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (current != workingLease)
                {
                    current.Dispose();
                }
                if (cloneLease)
                {
                    workingLease.Dispose();
                }
            }
        }
    }

    private static async ValueTask SEND_UDP_ASYNC(IConnection connection, IConnection.ITransport transport, [Borrowed] IBufferLease current, CancellationToken ct)
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
}
