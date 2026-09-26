// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.SDK.Options;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Provides streaming extension methods for <see cref="TransportSession"/> allowing packet multiplexing.
/// </summary>
public static class StreamExtensions
{
    /// <summary>
    /// Sends a request and returns an asynchronous stream of responses.
    /// The stream completes automatically when a response with <see cref="IPacketStreamable.IsEndOfStream"/> is received.
    /// </summary>
    /// <typeparam name="TResponse">The expected type of the response chunks.</typeparam>
    /// <param name="client">The connected client session.</param>
    /// <param name="request">The request packet initiating the stream.</param>
    /// <param name="options">Options for the request (e.g., encryption).</param>
    /// <param name="ct">The cancellation token to cancel the stream.</param>
    /// <param name="inactivityTimeoutMs">
    /// Milliseconds to wait for the next chunk before failing the stream with a <see cref="TimeoutException"/>.
    /// <c>0</c> (default) disables this check, preserving prior behavior — a server that stops sending
    /// chunks without disconnecting or sending a terminator hangs the enumeration indefinitely.
    /// </param>
    /// <returns>An asynchronous enumerable stream of responses.</returns>
    /// <exception cref="ArgumentNullException">Thrown if client or request is null.</exception>
    /// <exception cref="NetworkException">Thrown if the client is not connected.</exception>
    /// <exception cref="TimeoutException">
    /// No chunk arrived within <paramref name="inactivityTimeoutMs"/> since the last one (or since the start).
    /// </exception>
#pragma warning disable CA1068 // ct must stay before the new trailing param to preserve existing named-arg call sites.
    public static async IAsyncEnumerable<TResponse> StreamAsync<TResponse>(
        this ITransportSession client,
        IPacket request,
        RequestOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        int inactivityTimeoutMs = 0)
#pragma warning restore CA1068
        where TResponse : class, IPacket, IPacketStaticOpcode, IPacketStreamable
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        if (inactivityTimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeoutMs), inactivityTimeoutMs, $"{nameof(inactivityTimeoutMs)} must be >= 0.");
        }

        options ??= RequestOptions.Default;

        if (!client.IsConnected)
        {
            await RequestExtensions.AwaitReadyOrThrowAsync(client, options.TimeoutMs, typeof(TResponse).Name, ct).ConfigureAwait(false);
        }

        // Stamp here — before latching expectedSeqId and before subscribing — rather than letting
        // SendAsync stamp it later. SendAsync's own stamp only replaces a still-zero SequenceId, so
        // this is a no-op for callers who already assigned one; for callers who left it at 0, it
        // guarantees expectedSeqId and the value actually put on the wire are the same number. Without
        // this, a caller who leaves SequenceId unset would latch expectedSeqId = 0 here, SendAsync
        // would then stamp the wire packet with a fresh non-zero id, and every reply — including the
        // terminator — would be silently dropped by the SequenceId check below: no exception, the
        // `await foreach` simply never completes.
        if (client is TransportSession session)
        {
            session.StampSequenceIdIfUnset(request);
        }

        ushort expectedSeqId = request.Header.SequenceId;

        // Use Unbounded channel to prevent blocking the network reader thread.
        // If the consumer is too slow, memory will grow, which is the standard behavior for downloading streams.
        Channel<TResponse> channel = Channel.CreateUnbounded<TResponse>(new UnboundedChannelOptions
        {
            SingleWriter = false, // Network reader threads might be multiple depending on transport implementation
            SingleReader = true   // Only one consumer iterates the foreach loop
        });

        void OnMessageReceived(TResponse chunk)
        {
            if (chunk.Header.SequenceId != expectedSeqId)
            {
                DisposePacket(chunk);
                return;
            }

            // Write the chunk to the channel
            if (!channel.Writer.TryWrite(chunk))
            {
                DisposePacket(chunk);
                return;
            }

            // If this is the final chunk, complete the channel
            if (chunk.IsEndOfStream)
            {
                _ = channel.Writer.TryComplete();
            }
        }

        void OnDisconnected(object? sender, Exception ex) => channel.Writer.TryComplete(ex ?? new NetworkException("Client disconnected unexpectedly during stream."));

        IDisposable msgSub = client.On<TResponse>(OnMessageReceived, disposeAfter: false);
        client.OnDisconnected += OnDisconnected;

        using CancellationTokenSource? inactivityCts = inactivityTimeoutMs > 0 ? new CancellationTokenSource() : null;

        try
        {
            // Send the request
            await client.SendAsync(request, encrypt: options.Encrypt, ct: ct).ConfigureAwait(false);

            inactivityCts?.CancelAfter(inactivityTimeoutMs);

            // Yield the stream elements as they arrive
            IAsyncEnumerable<TResponse> reader = inactivityCts is null
                ? channel.Reader.ReadAllAsync(ct)
                : channel.Reader.ReadAllAsync(inactivityCts.Token);

            IAsyncEnumerator<TResponse> enumerator = reader.GetAsyncEnumerator();
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (inactivityCts is not null && inactivityCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"[SDK.StreamAsync<{typeof(TResponse).Name}>] Stream inactivity timeout — no chunk received within {inactivityTimeoutMs}ms.");
                    }

                    if (!hasNext)
                    {
                        yield break;
                    }

                    inactivityCts?.CancelAfter(inactivityTimeoutMs);
                    yield return enumerator.Current;
                }
            }
        }
        finally
        {
            msgSub.Dispose();
            client.OnDisconnected -= OnDisconnected;

            // Ensure the channel is completed if we exit early (e.g. cancellation)
            _ = channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Collects a stream into a list with a per-attempt timeout.
    /// </summary>
    /// <typeparam name="TResponse">The expected type of the response chunks.</typeparam>
    /// <param name="client">The connected client session.</param>
    /// <param name="requestFactory">Creates a fresh request packet for each attempt.</param>
    /// <param name="timeoutMs">The timeout in milliseconds for each attempt. Use 0 to wait indefinitely.</param>
    /// <param name="maxAttempts">The maximum number of attempts.</param>
    /// <param name="ct">The cancellation token for the full operation.</param>
    /// <returns>The collected stream items.</returns>
    public static async Task<List<TResponse>> CollectStreamAsync<TResponse>(
        this ITransportSession client,
        Func<IPacket> requestFactory,
        int timeoutMs = 9_000,
        int maxAttempts = 2,
        CancellationToken ct = default)
        where TResponse : class, IPacket, IPacketStaticOpcode, IPacketStreamable
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ValidateStreamHelperArguments(timeoutMs, maxAttempts);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            IPacket request = requestFactory();
            ArgumentNullException.ThrowIfNull(request);

            try
            {
                using CancellationTokenSource attemptCts = CreateAttemptTokenSource(timeoutMs, ct);
                List<TResponse> items = [];

                await foreach (TResponse item in client.StreamAsync<TResponse>(request, ct: attemptCts.Token).ConfigureAwait(false))
                {
                    if (IsExplicitTerminator(item))
                    {
                        DisposePacket(item);
                        continue;
                    }

                    items.Add(item);
                }

                return items;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutMs > 0)
            {
                lastException = ex;
            }
            finally
            {
                DisposePacket(request);
            }
        }

        throw new TimeoutException(
            $"[SDK.CollectStreamAsync<{typeof(TResponse).Name}>] No complete stream after {maxAttempts} attempt(s) (timeout={timeoutMs}ms each).",
            lastException);
    }

    /// <summary>
    /// Collects a stream into a de-duplicated list with a per-attempt timeout.
    /// </summary>
    /// <typeparam name="TResponse">The expected type of the response chunks.</typeparam>
    /// <typeparam name="TKey">The item key type.</typeparam>
    /// <param name="client">The connected client session.</param>
    /// <param name="requestFactory">Creates a fresh request packet for each attempt.</param>
    /// <param name="keySelector">Selects the de-duplication key.</param>
    /// <param name="timeoutMs">The timeout in milliseconds for each attempt. Use 0 to wait indefinitely.</param>
    /// <param name="maxAttempts">The maximum number of attempts.</param>
    /// <param name="ct">The cancellation token for the full operation.</param>
    /// <returns>The collected stream items.</returns>
    public static async Task<List<TResponse>> CollectStreamAsync<TResponse, TKey>(
        this ITransportSession client,
        Func<IPacket> requestFactory,
        Func<TResponse, TKey> keySelector,
        int timeoutMs = 9_000,
        int maxAttempts = 2,
        CancellationToken ct = default)
        where TResponse : class, IPacket, IPacketStaticOpcode, IPacketStreamable
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(keySelector);
        ValidateStreamHelperArguments(timeoutMs, maxAttempts);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            IPacket request = requestFactory();
            ArgumentNullException.ThrowIfNull(request);

            try
            {
                using CancellationTokenSource attemptCts = CreateAttemptTokenSource(timeoutMs, ct);
                List<TResponse> items = [];
                HashSet<TKey> seen = [];

                await foreach (TResponse item in client.StreamAsync<TResponse>(request, ct: attemptCts.Token).ConfigureAwait(false))
                {
                    if (IsExplicitTerminator(item))
                    {
                        DisposePacket(item);
                        continue;
                    }

                    if (seen.Add(keySelector(item)))
                    {
                        items.Add(item);
                    }
                    else
                    {
                        DisposePacket(item);
                    }
                }

                return items;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutMs > 0)
            {
                lastException = ex;
            }
            finally
            {
                DisposePacket(request);
            }
        }

        throw new TimeoutException(
            $"[SDK.CollectStreamAsync<{typeof(TResponse).Name}>] No complete stream after {maxAttempts} attempt(s) (timeout={timeoutMs}ms each).",
            lastException);
    }

    /// <summary>
    /// Streams items into a target list and invokes a throttled callback as batches arrive.
    /// </summary>
    /// <typeparam name="TResponse">The expected type of the response chunks.</typeparam>
    /// <typeparam name="TKey">The item key type.</typeparam>
    /// <param name="client">The connected client session.</param>
    /// <param name="requestFactory">Creates a fresh request packet for each attempt.</param>
    /// <param name="target">The target list to update.</param>
    /// <param name="keySelector">Selects the de-duplication key.</param>
    /// <param name="onBatch">Called when a batch threshold is reached.</param>
    /// <param name="batchCount">The number of new items that triggers <paramref name="onBatch"/>.</param>
    /// <param name="batchMs">The elapsed milliseconds that triggers <paramref name="onBatch"/>.</param>
    /// <param name="timeoutMs">The timeout in milliseconds for each attempt. Use 0 to wait indefinitely.</param>
    /// <param name="maxAttempts">The maximum number of attempts.</param>
    /// <param name="ct">The cancellation token for the full operation.</param>
    public static async Task StreamIntoAsync<TResponse, TKey>(
        this ITransportSession client,
        Func<IPacket> requestFactory,
        List<TResponse> target,
        Func<TResponse, TKey> keySelector,
        Action onBatch,
        int batchCount = 10,
        int batchMs = 100,
        int timeoutMs = 9_000,
        int maxAttempts = 2,
        CancellationToken ct = default)
        where TResponse : class, IPacket, IPacketStaticOpcode, IPacketStreamable
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(onBatch);

        if (batchCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchCount), batchCount, $"{nameof(batchCount)} must be > 0.");
        }

        if (batchMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchMs), batchMs, $"{nameof(batchMs)} must be >= 0.");
        }

        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(keySelector);
        ValidateStreamHelperArguments(timeoutMs, maxAttempts);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            IPacket request = requestFactory();
            ArgumentNullException.ThrowIfNull(request);

            try
            {
                using CancellationTokenSource attemptCts = CreateAttemptTokenSource(timeoutMs, ct);
                HashSet<TKey> seen = [];
                target.Clear();

                int pending = 0;
                long lastBatchTicks = System.Environment.TickCount64;

                await foreach (TResponse item in client.StreamAsync<TResponse>(request, ct: attemptCts.Token).ConfigureAwait(false))
                {
                    if (IsExplicitTerminator(item))
                    {
                        DisposePacket(item);
                        continue;
                    }

                    if (!seen.Add(keySelector(item)))
                    {
                        DisposePacket(item);
                        continue;
                    }

                    target.Add(item);
                    pending++;

                    long now = System.Environment.TickCount64;
                    if (pending >= batchCount || (batchMs > 0 && now - lastBatchTicks >= batchMs))
                    {
                        onBatch();
                        pending = 0;
                        lastBatchTicks = now;
                    }
                }

                if (pending > 0 || target.Count == 0)
                {
                    onBatch();
                }

                return;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeoutMs > 0)
            {
                lastException = ex;
                target.Clear();
                onBatch();
            }
            finally
            {
                DisposePacket(request);
            }
        }

        throw new TimeoutException(
            $"[SDK.StreamIntoAsync<{typeof(TResponse).Name}>] No complete stream after {maxAttempts} attempt(s) (timeout={timeoutMs}ms each).",
            lastException);
    }

    private static CancellationTokenSource CreateAttemptTokenSource(int timeoutMs, CancellationToken ct)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
        {
            cts.CancelAfter(timeoutMs);
        }

        return cts;
    }

    private static bool IsExplicitTerminator(IPacketStreamable packet)
        => packet.IsEndOfStream && packet is IPacketStreamTerminator { IsTerminator: true };

    private static void ValidateStreamHelperArguments(int timeoutMs, int maxAttempts)
    {
        if (timeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, $"{nameof(timeoutMs)} must be >= 0.");
        }

        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, $"{nameof(maxAttempts)} must be > 0.");
        }
    }

    private static void DisposePacket(IPacket packet)
    {
        if (packet is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
