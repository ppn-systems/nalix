// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Extensions;

namespace Nalix.SDK.Transport.Internal;

/// <summary>
/// One in-flight request-response exchange: it watches the session's inbound stream for the first
/// packet of <typeparamref name="TPkt"/> that satisfies a predicate, and completes when that packet
/// arrives, when the session drops, or when the caller's timeout expires.
/// </summary>
/// <typeparam name="TPkt">The response packet type.</typeparam>
/// <remarks>
/// <para>
/// This exists to keep the request path free of per-request closures. The previous implementation
/// built a lambda for the predicate, another for the handler, another for the disconnect handler,
/// one more for each unsubscribe, a display class for each of them, a <c>CompositeSubscription</c>
/// with its parameter array, a linked <see cref="CancellationTokenSource"/> and its timer
/// registration — for every single request. Here the state lives in fields and the two event
/// handlers are method groups bound once per instance.
/// </para>
/// <para>
/// Completion is single-shot and races are resolved by <see cref="Interlocked"/>: whichever of the
/// three outcomes gets there first wins, and the rest are no-ops.
/// </para>
/// </remarks>
internal sealed class PendingRequest<TPkt>
    where TPkt : class, IPacket, IPacketStaticOpcode
{
    private readonly TaskCompletionSource<TPkt> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ITransportSession _session;
    private readonly Func<TPkt, bool> _predicate;
    private readonly ushort _opcode;

    // Bound once. A method group in a += or -= builds a fresh delegate every time, so subscribing
    // and unsubscribing around one request would otherwise cost four delegate allocations.
    private readonly EventHandler<IBufferLease> _onMessage;
    private readonly EventHandler<Exception> _onDisconnected;

    private int _completed;

    internal PendingRequest(ITransportSession session, Func<TPkt, bool> predicate)
    {
        _session = session;
        _predicate = predicate;
        _opcode = TPkt.StaticOpCode;

        _onMessage = this.OnMessageReceived;
        _onDisconnected = this.OnDisconnected;
    }

    /// <summary>The task that completes with the matching packet, or faults.</summary>
    internal Task<TPkt> Task => _completion.Task;

    /// <summary>Starts watching the session's inbound stream.</summary>
    internal void Subscribe()
    {
        _session.OnMessageReceived += _onMessage;
        _session.OnDisconnected += _onDisconnected;
    }

    /// <summary>Stops watching. Safe to call more than once.</summary>
    internal void Unsubscribe()
    {
        _session.OnMessageReceived -= _onMessage;
        _session.OnDisconnected -= _onDisconnected;
    }

    /// <summary>Faults the exchange, unless it has already settled.</summary>
    internal void TrySetException(Exception error)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _ = _completion.TrySetException(error);
        }
    }

    private void OnMessageReceived(Object? sender, IBufferLease buffer)
    {
        // The session owns the lease and disposes it after this returns; never dispose it here.
        if (buffer.Length < PacketConstants.HeaderSize)
        {
            return;
        }

        ref readonly PacketHeader header = ref buffer.Span.AsHeaderRef();
        if (header.OpCode != _opcode)
        {
            return;
        }

        try
        {
            IPacket packet = PacketRegistry.Deserialize(buffer.Span);
            packet.Header = header;

            Boolean delivered = false;

            try
            {
                if (packet is not TPkt typed)
                {
                    return;
                }

                Boolean matches;

                try
                {
                    matches = _predicate(typed);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    this.TrySetException(ex);
                    return;
                }

                if (!matches)
                {
                    return;
                }

                // Only the first arriving thread hands the packet to the caller; the caller then
                // owns it, which is why it is not disposed on this path.
                if (Interlocked.Exchange(ref _completed, 1) != 0)
                {
                    return;
                }

                delivered = true;
                _session.OnMessageReceived -= _onMessage;

                _ = _completion.TrySetResult(typed);
            }
            finally
            {
                if (!delivered && packet is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            Trace.TraceError(
                "Nalix.SDK.PendingRequest<{0}> failed to handle an inbound frame: {1}",
                typeof(TPkt).Name,
                ex);
        }
    }

    private void OnDisconnected(Object? sender, Exception error)
        => this.TrySetException(new NetworkException(
            $"Disconnected while waiting for {typeof(TPkt).Name}.",
            error ?? new InvalidOperationException("The transport session was disconnected.")));
}
