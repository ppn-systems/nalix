// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.SDK.Options;
using Nalix.SDK.Transport.Internal;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Provides request-response helpers for <see cref="TransportSession"/>.
/// Combines a one-shot subscription with a send operation so callers can
/// <c>await</c> a typed reply without wiring boilerplate by hand.
/// </summary>
/// <remarks>
/// <para>
/// Internally delegates the subscribe -> await -> timeout -> unsubscribe cycle to
/// <see cref="PacketAwaiter"/>, which handles deserialization errors, predicate
/// exceptions, and disconnect guards consistently across all SDK extension methods.
/// </para>
/// <para>
/// <b>Threading model:</b> callbacks are invoked on the FrameReader background thread.
/// Marshal to the main thread before touching Unity GameObjects or WPF/MAUI UI controls.
/// </para>
/// <para>
/// <b>Retry:</b> only <see cref="TimeoutException"/> triggers a retry.
/// Fatal errors (send failure, disconnect) propagate immediately.
/// <br/>
/// <b>WARNING:</b> Retrying requests is only safe for idempotent requests. Since a timeout
/// does not prove the server did not receive/process the request, retrying non-idempotent
/// requests can result in duplicate side effects.
/// </para>
/// <para>
/// <b>Auto-reconnect:</b> when <see cref="Options.TransportOptions.AutoReconnectEnabled"/> is set and the
/// session is not connected at call time, the request first waits (bounded by the request timeout)
/// for the in-progress reconnect and re-authentication to finish. A request that is already in
/// flight when the transport drops fails with <see cref="NetworkException"/> and is <b>not</b>
/// replayed after the reconnect: the server may already have processed it, so only the caller
/// can decide whether re-sending is safe. Await <see cref="TransportSession.WaitUntilReadyAsync"/>
/// and re-issue idempotent requests if needed.
/// </para>
/// <para>
/// <see cref="RequestAsync{TResponse}"/> is the safe, race-condition-free way to
/// send a packet and await a correlated reply. It subscribes <b>before</b> sending — eliminating
/// the window where the server response could arrive before the local handler is registered.
/// </para>
/// <para>
/// Compared to calling <c>SendAsync</c> then <c>AwaitPacketAsync</c> sequentially, this method
/// guarantees no missed responses even under high concurrency or very low-latency servers.
/// </para>
/// </remarks>
[SkipLocalsInit]
public static class RequestExtensions
{
    /// <summary>
    /// Sends <paramref name="request"/> and waits for the first incoming packet
    /// of type <typeparamref name="TResponse"/> that satisfies <paramref name="predicate"/>.
    /// Retry and encryption behaviour is controlled by <paramref name="options"/>.
    /// </summary>
    /// <typeparam name="TResponse">Expected response packet type.</typeparam>
    /// <param name="client">Connected client session.</param>
    /// <param name="request">Packet to send. Must not be <see langword="null"/>.</param>
    /// <param name="options">
    /// Timeout, retry, and encryption settings.
    /// <see langword="null"/> falls back to <see cref="RequestOptions.Default"/>.
    /// </param>
    /// <param name="predicate">
    /// Optional response filter. <see langword="null"/> accepts the first
    /// <typeparamref name="TResponse"/> that arrives.
    /// </param>
    /// <param name="ct">Cancellation token for the entire operation (all attempts).</param>
    /// <returns>The first matching response packet.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NetworkException">
    /// Client not connected, transport disconnected during send/wait, or <c>SendAsync</c> returned <see langword="false"/>.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// No response arrived within the allotted timeout on all attempts.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled, or the connection dropped mid-wait.
    /// </exception>
    /// <example>
    /// <code>
    /// // 1. Simplest — default options
    /// var reply = await client.RequestAsync&lt;LoginResponse&gt;(loginRequest);
    ///
    /// // 2. Custom options, fluent
    /// var opts = RequestOptions.Default
    ///     .WithTimeout(3_000)
    ///     .WithRetry(2)
    ///     .WithEncrypt();
    /// var reply = await client.RequestAsync&lt;LoginResponse&gt;(loginRequest, opts);
    ///
    /// // 3. With correlation predicate
    /// var reply = await client.RequestAsync&lt;TradeResponse&gt;(
    ///     tradeRequest,
    ///     RequestOptions.Default.WithTimeout(2_000),
    ///     predicate: r => r.RequestId == tradeRequest.Id);
    /// </code>
    /// </example>
    public static async ValueTask<TResponse> RequestAsync<TResponse>(
        this ITransportSession client,
        IPacket request,
        RequestOptions? options = null,
        Func<TResponse, bool>? predicate = null,
        CancellationToken ct = default)
        where TResponse : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        options ??= RequestOptions.Default;
        options.Validate();

        if (!client.IsConnected)
        {
            await AwaitReadyOrThrowAsync(client, options.TimeoutMs, typeof(TResponse).Name, ct).ConfigureAwait(false);
        }

        Exception? lastException = null;
        int totalAttempts = options.RetryCount + 1;
        Func<TResponse, bool> effectivePredicate = predicate ?? (_ => true);

        for (int attempt = 1; attempt <= totalAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Delegate the full subscribe -> send -> await -> timeout -> unsubscribe cycle
                // to PACKET_AWAITER, which handles deserialization errors, predicate exceptions,
                // and disconnect guards consistently across all SDK helpers.
                TResponse result = await PacketAwaiter.AwaitAsync(
                    client,
                    predicate: effectivePredicate,
                    timeoutMs: options.TimeoutMs,
                    sendAsync: token => client.SendAsync(request, encrypt: options.Encrypt, token),
                    ct).ConfigureAwait(false);

                return result;
            }
            catch (TimeoutException tex) when (attempt < totalAttempts)
            {
                // Only TimeoutException is retryable.
                // OperationCanceledException, NetworkException, etc. propagate immediately.
                lastException = tex;
            }
        }

        throw new TimeoutException(
            $"[SDK.RequestAsync<{typeof(TResponse).Name}>] No response after {totalAttempts} attempt(s) (timeout={options.TimeoutMs}ms each).", lastException);
    }

    /// <summary>
    /// Non-throwing counterpart to <see cref="RequestAsync{TResponse}"/>: classifies the outcome
    /// instead of throwing, so UI-facing callers (e.g. Blazor) can branch without try/catch.
    /// Retry/timeout/encryption semantics are identical to <see cref="RequestAsync{TResponse}"/>.
    /// </summary>
    /// <typeparam name="TResponse">Expected response packet type.</typeparam>
    /// <param name="client">Connected client session.</param>
    /// <param name="request">Packet to send. Must not be <see langword="null"/>.</param>
    /// <param name="options">
    /// Timeout, retry, and encryption settings. <see langword="null"/> falls back to <see cref="RequestOptions.Default"/>.
    /// </param>
    /// <param name="predicate">Optional response filter. <see langword="null"/> accepts the first matching packet.</param>
    /// <param name="ct">Cancellation token for the entire operation (all attempts).</param>
    /// <returns>
    /// A <see cref="RequestOutcome{T}"/> describing success, timeout, disconnection, or another failure.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async ValueTask<RequestOutcome<TResponse>> TryRequestAsync<TResponse>(
        this ITransportSession client,
        IPacket request,
        RequestOptions? options = null,
        Func<TResponse, bool>? predicate = null,
        CancellationToken ct = default)
        where TResponse : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        options ??= RequestOptions.Default;
        options.Validate();

        if (!client.IsConnected)
        {
            try
            {
                await AwaitReadyOrThrowAsync(client, options.TimeoutMs, typeof(TResponse).Name, ct).ConfigureAwait(false);
            }
            catch (NetworkException ex)
            {
                return RequestOutcome<TResponse>.Fail(RequestOutcomeKind.NotConnected, ex);
            }
        }

        Exception? lastException = null;
        int totalAttempts = options.RetryCount + 1;
        Func<TResponse, bool> effectivePredicate = predicate ?? (_ => true);

        for (int attempt = 1; attempt <= totalAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                TResponse result = await PacketAwaiter.AwaitAsync(
                    client,
                    predicate: effectivePredicate,
                    timeoutMs: options.TimeoutMs,
                    sendAsync: token => client.SendAsync(request, encrypt: options.Encrypt, token),
                    ct).ConfigureAwait(false);

                return RequestOutcome<TResponse>.Ok(result);
            }
            catch (TimeoutException tex) when (attempt < totalAttempts)
            {
                lastException = tex;
            }
            catch (TimeoutException tex)
            {
                return RequestOutcome<TResponse>.Fail(RequestOutcomeKind.TimedOut, tex);
            }
            catch (NetworkException nex)
            {
                return RequestOutcome<TResponse>.Fail(RequestOutcomeKind.Failed, nex);
            }
        }

        return RequestOutcome<TResponse>.Fail(
            RequestOutcomeKind.TimedOut,
            new TimeoutException(
                $"[SDK.TryRequestAsync<{typeof(TResponse).Name}>] No response after {totalAttempts} attempt(s) (timeout={options.TimeoutMs}ms each).",
                lastException));
    }

    /// <summary>
    /// When the session is a <see cref="TransportSession"/> with auto-reconnect enabled, awaits
    /// the in-flight reconnect (bounded by <paramref name="timeoutMs"/>). Otherwise throws
    /// immediately, preserving the pre-existing "not connected" behavior.
    /// </summary>
    internal static async ValueTask AwaitReadyOrThrowAsync(
        ITransportSession client, int timeoutMs, string responseTypeName, CancellationToken ct)
    {
        Internal.ReconnectSupervisor supervisor = (client as TransportSession)?.ReconnectSupervisor
            ?? throw new NetworkException($"[SDK.RequestAsync<{responseTypeName}>] Client is not connected.");

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
        {
            linkedCts.CancelAfter(timeoutMs);
        }

        try
        {
            await supervisor.ReadyAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NetworkException($"[SDK.RequestAsync<{responseTypeName}>] Client is not connected (reconnect timed out).");
        }
    }
}
