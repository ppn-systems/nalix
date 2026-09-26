// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
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
/// <b>Instance pooling:</b> instances are rented from a small per-<typeparamref name="TPkt"/> pool
/// (<see cref="Rent"/>/<see cref="Return"/>) instead of allocated fresh per request, so the two event
/// handler delegates and the <see cref="TaskCompletionSource{TPkt}"/> are the only things a live
/// request still needs from the heap — the wrapper object and its delegates are reused across
/// requests. A request is only returned to the pool once its <see cref="Task"/> has been fully
/// observed (see <see cref="PacketAwaiter"/>), never while still in flight.
/// </para>
/// <para>
/// Completion is single-shot and races are resolved by <see cref="Interlocked"/>: whichever of the
/// three outcomes gets there first wins, and the rest are no-ops.
/// </para>
/// </remarks>
internal sealed class PendingRequest<TPkt>
    where TPkt : class, IPacket, IPacketStaticOpcode
{
    // Bounded so a burst of concurrent requests does not grow the pool without limit; beyond this,
    // Rent() falls back to allocating a fresh instance exactly as before pooling existed.
    private const int MaxPooled = 64;

    private static readonly ConcurrentQueue<PendingRequest<TPkt>> s_pool = new();

    private TaskCompletionSource<TPkt> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ITransportSession? _session;
    private Func<TPkt, bool>? _predicate;
    private ushort _opcode;

    // Bound once per pooled instance (not once per request). A method group in a += or -= builds a
    // fresh delegate every time, so subscribing and unsubscribing around one request would otherwise
    // cost four delegate allocations; pooling the instance means these are created at most
    // MaxPooled times total across the process, not once per request.
    private readonly EventHandler<IBufferLease> _onMessage;
    private readonly EventHandler<Exception> _onDisconnected;

    private int _completed;

    private PendingRequest()
    {
        _onMessage = this.OnMessageReceived;
        _onDisconnected = this.OnDisconnected;
    }

    /// <summary>
    /// Rents a pooled instance (or allocates one if the pool is empty) and binds it to
    /// <paramref name="session"/> and <paramref name="predicate"/> for one request.
    /// </summary>
    internal static PendingRequest<TPkt> Rent(ITransportSession session, Func<TPkt, bool> predicate)
    {
        if (!s_pool.TryDequeue(out PendingRequest<TPkt>? pending))
        {
            pending = new PendingRequest<TPkt>();
        }

        pending._session = session;
        pending._predicate = predicate;
        pending._opcode = TPkt.StaticOpCode;
        pending._completed = 0;

        // A completed TaskCompletionSource cannot be reused for a new await, so each rental gets a
        // fresh one. This — plus the Task<TPkt> it wraps — is the one per-request heap allocation
        // pooling cannot remove: the awaited result has to live in a real async completion object.
        pending._completion = new TaskCompletionSource<TPkt>(TaskCreationOptions.RunContinuationsAsynchronously);

        return pending;
    }

    /// <summary>
    /// Clears per-request state and returns the instance to the pool for reuse. Only call this once
    /// <see cref="Task"/> has been fully awaited (or faulted) — never while a caller might still be
    /// observing it.
    /// </summary>
    internal void Return()
    {
        _session = null;
        _predicate = null;

        if (s_pool.Count < MaxPooled)
        {
            s_pool.Enqueue(this);
        }
    }

    /// <summary>The task that completes with the matching packet, or faults.</summary>
    internal Task<TPkt> Task => _completion.Task;

    /// <summary>Starts watching the session's inbound stream.</summary>
    internal void Subscribe()
    {
        _session!.OnMessageReceived += _onMessage;
        _session.OnDisconnected += _onDisconnected;
    }

    /// <summary>Stops watching. Safe to call more than once.</summary>
    internal void Unsubscribe()
    {
        _session!.OnMessageReceived -= _onMessage;
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
                    matches = _predicate!(typed);
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
                _session!.OnMessageReceived -= _onMessage;

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
