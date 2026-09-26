// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Provides the client-side counterpart of a "header + N sub-streams" server response: one request
/// answered by a single header packet followed by one or more streams of item packets, all
/// correlated by the request's <c>SequenceId</c> — the shape a detail page uses when it needs one
/// summary object plus one or more related lists in a single round trip (an assessment with its
/// entries, an observation with its areas/next-steps/media, a support ticket with its messages).
/// </summary>
/// <remarks>
/// Before this existed, every such screen hand-rolled the same five steps: stamp a
/// <c>SequenceId</c>, open a <see cref="TaskCompletionSource{T}"/> per response type, subscribe them
/// all via <see cref="TransportSessionSubscriptions.On{TPacket}"/> before sending, apply one shared
/// timeout to every TCS, then await the header followed by each stream. Each copy risked a subtly
/// different mistake (a TCS built without
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, a timeout that only covered some
/// of the TCS's, a missed disposal of the subscription). This type is that boilerplate written once,
/// per arity — deliberately NOT collapsed into one reflection-driven core, since that would defeat
/// AOT/trimming on the WASM targets this SDK ships to.
/// </remarks>
public static class HeaderStreamExtensions
{
    /// <summary>
    /// Sends <paramref name="request"/> and awaits a header packet plus one correlated item stream.
    /// </summary>
    /// <typeparam name="THeader">The header/summary response type.</typeparam>
    /// <typeparam name="T1">The stream item type.</typeparam>
    /// <param name="client">The connected client session.</param>
    /// <param name="request">
    /// The request packet. Its <c>SequenceId</c> is stamped here if left at <c>0</c> — the caller
    /// never needs to mint one itself.
    /// </param>
    /// <param name="isItem1Valid">
    /// Returns <see langword="false"/> for a sentinel/empty item that must not be added to the
    /// result list (the shape servers use to signal "the stream had zero real rows" while still
    /// sending a terminator).
    /// </param>
    /// <param name="timeoutMs">
    /// How long to wait for the header, and separately how long to wait for the stream to complete
    /// once it starts being awaited.
    /// </param>
    /// <param name="encrypt">Per-send encryption override, or <see langword="null"/> for the session default.</param>
    /// <param name="ct">Cancels the exchange.</param>
    /// <returns>The header packet and the accumulated, filtered stream items.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="client"/>, <paramref name="request"/>, or
    /// <paramref name="isItem1Valid"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NetworkException">Thrown when the client is not connected, or the send fails.</exception>
    /// <exception cref="TimeoutException">Thrown when the header or the stream does not complete in time.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public static async Task<(THeader Header, List<T1> Items1)> RequestWithStreamAsync<THeader, T1>(
        this ITransportSession client,
        IPacket request,
        Func<T1, bool> isItem1Valid,
        int timeoutMs = 15_000,
        bool? encrypt = null,
        CancellationToken ct = default)
        where THeader : class, IPacket, IPacketStaticOpcode
        where T1 : class, IPacket, IPacketStaticOpcode, IPacketStreamable
    {
        ArgumentNullException.ThrowIfNull(isItem1Valid);
        ushort seq = StampAndValidate(client, request, timeoutMs);

        TaskCompletionSource<THeader> headerTcs = NewTcs<THeader>();
        (TaskCompletionSource<List<T1>> tcs1, List<T1> buf1) = NewStreamTcs<T1>();

        using CompositeSubscription subs = client.Subscribe(
            client.On(MakeHeaderHandler(headerTcs, seq)),
            client.On(MakeItemHandler(buf1, tcs1, seq, isItem1Valid)));

        await SendOrFailAsync(client, request, encrypt, ct, headerTcs, tcs1).ConfigureAwait(false);

        using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(timeoutMs));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        THeader header = await AwaitOrTimeout(headerTcs.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(THeader)).ConfigureAwait(false);
        List<T1> items1 = await AwaitOrTimeout(tcs1.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(T1)).ConfigureAwait(false);

        return (header, items1);
    }

    /// <summary>
    /// Sends <paramref name="request"/> and awaits a header packet plus two correlated item streams.
    /// </summary>
    /// <remarks>See <see cref="RequestWithStreamAsync{THeader, T1}"/> for the full parameter contract.</remarks>
    public static async Task<(THeader Header, List<T1> Items1, List<T2> Items2)> RequestWithStreamAsync<THeader, T1, T2>(
        this ITransportSession client,
        IPacket request,
        Func<T1, bool> isItem1Valid,
        Func<T2, bool> isItem2Valid,
        int timeoutMs = 15_000,
        bool? encrypt = null,
        CancellationToken ct = default)
        where THeader : class, IPacket, IPacketStaticOpcode
        where T1 : class, IPacket, IPacketStaticOpcode, IPacketStreamable
        where T2 : class, IPacket, IPacketStaticOpcode, IPacketStreamable
    {
        ArgumentNullException.ThrowIfNull(isItem1Valid);
        ArgumentNullException.ThrowIfNull(isItem2Valid);
        ushort seq = StampAndValidate(client, request, timeoutMs);

        TaskCompletionSource<THeader> headerTcs = NewTcs<THeader>();
        (TaskCompletionSource<List<T1>> tcs1, List<T1> buf1) = NewStreamTcs<T1>();
        (TaskCompletionSource<List<T2>> tcs2, List<T2> buf2) = NewStreamTcs<T2>();

        using CompositeSubscription subs = client.Subscribe(
            client.On(MakeHeaderHandler(headerTcs, seq)),
            client.On(MakeItemHandler(buf1, tcs1, seq, isItem1Valid)),
            client.On(MakeItemHandler(buf2, tcs2, seq, isItem2Valid)));

        await SendOrFailAsync(client, request, encrypt, ct, headerTcs, tcs1, tcs2).ConfigureAwait(false);

        using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(timeoutMs));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        THeader header = await AwaitOrTimeout(headerTcs.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(THeader)).ConfigureAwait(false);
        List<T1> items1 = await AwaitOrTimeout(tcs1.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(T1)).ConfigureAwait(false);
        List<T2> items2 = await AwaitOrTimeout(tcs2.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(T2)).ConfigureAwait(false);

        return (header, items1, items2);
    }

    /// <summary>
    /// Sends <paramref name="request"/> and awaits a header packet plus three correlated item streams.
    /// </summary>
    /// <remarks>See <see cref="RequestWithStreamAsync{THeader, T1}"/> for the full parameter contract.</remarks>
    public static async Task<(THeader Header, List<T1> Items1, List<T2> Items2, List<T3> Items3)> RequestWithStreamAsync<THeader, T1, T2, T3>(
        this ITransportSession client,
        IPacket request,
        Func<T1, bool> isItem1Valid,
        Func<T2, bool> isItem2Valid,
        Func<T3, bool> isItem3Valid,
        int timeoutMs = 15_000,
        bool? encrypt = null,
        CancellationToken ct = default)
        where THeader : class, IPacket, IPacketStaticOpcode
        where T1 : class, IPacket, IPacketStaticOpcode, IPacketStreamable
        where T2 : class, IPacket, IPacketStaticOpcode, IPacketStreamable
        where T3 : class, IPacket, IPacketStaticOpcode, IPacketStreamable
    {
        ArgumentNullException.ThrowIfNull(isItem1Valid);
        ArgumentNullException.ThrowIfNull(isItem2Valid);
        ArgumentNullException.ThrowIfNull(isItem3Valid);
        ushort seq = StampAndValidate(client, request, timeoutMs);

        TaskCompletionSource<THeader> headerTcs = NewTcs<THeader>();
        (TaskCompletionSource<List<T1>> tcs1, List<T1> buf1) = NewStreamTcs<T1>();
        (TaskCompletionSource<List<T2>> tcs2, List<T2> buf2) = NewStreamTcs<T2>();
        (TaskCompletionSource<List<T3>> tcs3, List<T3> buf3) = NewStreamTcs<T3>();

        using CompositeSubscription subs = client.Subscribe(
            client.On(MakeHeaderHandler(headerTcs, seq)),
            client.On(MakeItemHandler(buf1, tcs1, seq, isItem1Valid)),
            client.On(MakeItemHandler(buf2, tcs2, seq, isItem2Valid)),
            client.On(MakeItemHandler(buf3, tcs3, seq, isItem3Valid)));

        await SendOrFailAsync(client, request, encrypt, ct, headerTcs, tcs1, tcs2, tcs3).ConfigureAwait(false);

        using CancellationTokenSource timeoutCts = new(TimeSpan.FromMilliseconds(timeoutMs));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        THeader header = await AwaitOrTimeout(headerTcs.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(THeader)).ConfigureAwait(false);
        List<T1> items1 = await AwaitOrTimeout(tcs1.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(T1)).ConfigureAwait(false);
        List<T2> items2 = await AwaitOrTimeout(tcs2.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(T2)).ConfigureAwait(false);
        List<T3> items3 = await AwaitOrTimeout(tcs3.Task, linked.Token, timeoutCts.Token, timeoutMs, typeof(T3)).ConfigureAwait(false);

        return (header, items1, items2, items3);
    }

    private static ushort StampAndValidate(ITransportSession client, IPacket request, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        if (timeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, $"{nameof(timeoutMs)} must be > 0.");
        }

        if (!client.IsConnected)
        {
            throw new NetworkException("Client not connected.");
        }

        // Same reasoning as StreamExtensions.StreamAsync: stamp before latching the id we correlate
        // replies on, so a caller who leaves SequenceId at 0 does not silently wait forever for
        // replies that went out stamped with a different id by SendAsync.
        if (client is TransportSession session)
        {
            session.StampSequenceIdIfUnset(request);
        }

        return request.Header.SequenceId;
    }

    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static (TaskCompletionSource<List<T>> Tcs, List<T> Buffer) NewStreamTcs<T>()
        => (NewTcs<List<T>>(), []);

    private static Action<THeader> MakeHeaderHandler<THeader>(TaskCompletionSource<THeader> tcs, ushort seq)
        where THeader : class, IPacket
        => header =>
        {
            if (header.Header.SequenceId == seq)
            {
                _ = tcs.TrySetResult(header);
            }
        };

    private static Action<T> MakeItemHandler<T>(
        List<T> buffer, TaskCompletionSource<List<T>> tcs, ushort seq, Func<T, bool> isValid)
        where T : class, IPacket, IPacketStreamable
        => item =>
        {
            if (item.Header.SequenceId != seq)
            {
                return;
            }

            if (isValid(item))
            {
                buffer.Add(item);
            }

            if (item.IsEndOfStream)
            {
                _ = tcs.TrySetResult(buffer);
            }
        };

    /// <summary>
    /// Sends the request and, if the send itself fails, faults the header TCS and the stream TCS
    /// with the same wrapped exception instead of leaving them to hang until the caller's timeout —
    /// mirroring how <see cref="PacketAwaiter"/> handles a failed send phase for a single-response
    /// request. One statically-typed overload per arity, deliberately not unified via <c>object</c>
    /// or <c>dynamic</c>, which would defeat AOT/trimming on the WASM targets this SDK ships to.
    /// </summary>
    private static async Task SendOrFailAsync<THeader, T1>(
        ITransportSession client, IPacket request, bool? encrypt, CancellationToken ct,
        TaskCompletionSource<THeader> headerTcs, TaskCompletionSource<List<T1>> tcs1)
    {
        try
        {
            await client.SendAsync(request, encrypt, ct).ConfigureAwait(false);
        }
        catch (Exception sendEx) when (ExceptionClassifier.IsNonFatal(sendEx))
        {
            NetworkException wrapped = WrapSendFailure<THeader>(sendEx);
            _ = headerTcs.TrySetException(wrapped);
            _ = tcs1.TrySetException(wrapped);
            throw wrapped;
        }
    }

    private static async Task SendOrFailAsync<THeader, T1, T2>(
        ITransportSession client, IPacket request, bool? encrypt, CancellationToken ct,
        TaskCompletionSource<THeader> headerTcs, TaskCompletionSource<List<T1>> tcs1, TaskCompletionSource<List<T2>> tcs2)
    {
        try
        {
            await client.SendAsync(request, encrypt, ct).ConfigureAwait(false);
        }
        catch (Exception sendEx) when (ExceptionClassifier.IsNonFatal(sendEx))
        {
            NetworkException wrapped = WrapSendFailure<THeader>(sendEx);
            _ = headerTcs.TrySetException(wrapped);
            _ = tcs1.TrySetException(wrapped);
            _ = tcs2.TrySetException(wrapped);
            throw wrapped;
        }
    }

    private static async Task SendOrFailAsync<THeader, T1, T2, T3>(
        ITransportSession client, IPacket request, bool? encrypt, CancellationToken ct,
        TaskCompletionSource<THeader> headerTcs, TaskCompletionSource<List<T1>> tcs1,
        TaskCompletionSource<List<T2>> tcs2, TaskCompletionSource<List<T3>> tcs3)
    {
        try
        {
            await client.SendAsync(request, encrypt, ct).ConfigureAwait(false);
        }
        catch (Exception sendEx) when (ExceptionClassifier.IsNonFatal(sendEx))
        {
            NetworkException wrapped = WrapSendFailure<THeader>(sendEx);
            _ = headerTcs.TrySetException(wrapped);
            _ = tcs1.TrySetException(wrapped);
            _ = tcs2.TrySetException(wrapped);
            _ = tcs3.TrySetException(wrapped);
            throw wrapped;
        }
    }

    private static NetworkException WrapSendFailure<THeader>(Exception sendEx) => sendEx is InvalidOperationException
        ? new NetworkException($"Disconnected while sending {typeof(THeader).Name}.", sendEx)
        : new NetworkException($"Failed to send {typeof(THeader).Name} request.", sendEx);

    private static async Task<T> AwaitOrTimeout<T>(
        Task<T> task, CancellationToken linked, CancellationToken timeoutToken, int timeoutMs, Type responseType)
    {
        try
        {
            return await task.WaitAsync(linked).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No {responseType.Name} received within {timeoutMs} ms.");
        }
    }
}
