// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.SDK.Transport.Extensions;

#if DEBUG
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Nalix.SDK.Tests")]
#endif

namespace Nalix.SDK.Transport.Internal;

/// <summary>
/// Internal helper that encapsulates the recurring boilerplate shared by all
/// "subscribe -> await matching packet -> timeout -> unsubscribe" operations.
/// </summary>
internal static class PacketAwaiter
{
    /// <summary>
    /// Subscribes for a matching packet, invokes <paramref name="sendAsync"/>,
    /// and waits until the first packet of type <typeparamref name="TPkt"/> that
    /// satisfies <paramref name="predicate"/> arrives — or the operation times out / is canceled.
    /// </summary>
    /// <typeparam name="TPkt"></typeparam>
    /// <param name="client"></param>
    /// <param name="predicate"></param>
    /// <param name="timeoutMs"></param>
    /// <param name="sendAsync"></param>
    /// <param name="ct"></param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="TimeoutException"></exception>
    /// <exception cref="OperationCanceledException"></exception>
    public static ValueTask<TPkt> AwaitAsync<TPkt>(
        ITransportSession client, Func<TPkt, bool> predicate,
        int timeoutMs, Func<CancellationToken, Task> sendAsync, CancellationToken ct)
        where TPkt : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(sendAsync);

        return CoreAsync(client, predicate, timeoutMs, request: null, encrypt: null, sendAsync, ct);
    }

    /// <summary>
    /// Subscribes for a matching packet, sends <paramref name="request"/>, and waits for the
    /// response — the same exchange as the delegate-based overload, without the caller having to
    /// build a send closure.
    /// </summary>
    /// <typeparam name="TPkt">The response packet type.</typeparam>
    /// <param name="client">The session to send on and listen to.</param>
    /// <param name="predicate">Decides whether an arriving packet is the response.</param>
    /// <param name="timeoutMs">How long to wait; 0 means forever.</param>
    /// <param name="request">The packet to send once the subscription is in place.</param>
    /// <param name="encrypt">Per-send encryption override, or <see langword="null"/> for the session default.</param>
    /// <param name="ct">Cancels the exchange.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="timeoutMs"/> is negative.</exception>
    /// <exception cref="TimeoutException">Thrown when no matching packet arrives in time.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public static ValueTask<TPkt> AwaitAsync<TPkt>(
        ITransportSession client, Func<TPkt, bool> predicate,
        int timeoutMs, IPacket request, bool? encrypt, CancellationToken ct)
        where TPkt : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(request);

        return CoreAsync(client, predicate, timeoutMs, request, encrypt, sendAsync: null, ct);
    }

    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<TPkt> CoreAsync<TPkt>(
        ITransportSession client, Func<TPkt, bool> predicate, int timeoutMs,
        IPacket? request, bool? encrypt, Func<CancellationToken, Task>? sendAsync, CancellationToken ct)
        where TPkt : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(predicate);

        if (timeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be >= 0 (0 = infinite)");
        }

        // Rented, not allocated: PendingRequest<TPkt> and its two event-handler delegates come from a
        // small per-type pool instead of the heap on every call (see PendingRequest<TPkt> remarks).
        PendingRequest<TPkt> pending = PendingRequest<TPkt>.Rent(client, predicate);
        pending.Subscribe();

        try
        {
            try
            {
                Task send = sendAsync is not null
                    ? sendAsync(ct)
                    : request is not null
                        ? client.SendAsync(request, encrypt, ct)
                        : Task.CompletedTask;

                await send.ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No {typeof(TPkt).Name} received within {timeoutMs} ms (send phase).");
            }
            catch (Exception sendEx) when (ExceptionClassifier.IsNonFatal(sendEx))
            {
                if (sendEx is InvalidOperationException)
                {
                    NetworkException wrapped = new(
                        $"Disconnected while sending {typeof(TPkt).Name}.", sendEx);

                    pending.TrySetException(wrapped);
                    throw wrapped;
                }

                pending.TrySetException(sendEx);
                throw;
            }

            try
            {
                // Task.WaitAsync arms the timer and the cancellation hook itself, which is why this
                // path builds no linked CancellationTokenSource, CancelAfter timer or registration.
                return timeoutMs > 0
                    ? await pending.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false)
                    : await pending.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No {typeof(TPkt).Name} received within {timeoutMs} ms.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }
        finally
        {
            pending.Unsubscribe();

            // Safe here: the Task has already been fully awaited (or thrown) above, so nothing
            // still holds a reference to this instance's in-flight state.
            pending.Return();
        }
    }

}
