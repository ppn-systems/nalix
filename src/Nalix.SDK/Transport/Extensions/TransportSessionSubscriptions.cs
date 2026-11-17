// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Extensions;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Convenience subscriptions for <see cref="TransportSession"/> to reduce boilerplate.
/// </summary>
/// <remarks>
/// <para>
/// Lease ownership contract: the <see cref="TransportSession"/> owns the
/// <see cref="IBufferLease"/> and will dispose it after event invocation.
/// Handlers must NOT dispose the lease; use Retain() if persistence is needed.
/// </para>
/// <para>
/// Handler exceptions are caught and logged; they are never re-thrown so that the
/// underlying <c>FrameReader</c> receive loop is never faulted by subscriber code.
/// </para>
/// </remarks>
[SkipLocalsInit]
public static class TransportSessionSubscriptions
{
    // ── On<TPacket> ──────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to strongly-typed packets and ignores non-matching packets.
    /// </summary>
    /// <typeparam name="TPacket">The packet type to receive.</typeparam>
    /// <param name="client">The transport session to subscribe to.</param>
    /// <param name="handler">The callback invoked for each received packet.</param>
    /// <param name="disposeAfter">Whether to dispose the packet after the handler returns.</param>
    /// <returns>An <see cref="IDisposable"/> that removes the subscription when disposed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IDisposable On<TPacket>(this ITransportSession client, Action<TPacket> handler, bool disposeAfter = true)
        where TPacket : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(handler);

        ushort targetOpCode = TPacket.StaticOpCode;

        void Wrapper(object? _, IBufferLease buffer)
        {
            // Cheap check: if the opcode doesn't match, don't even try to deserialize.
            // This prevents O(N*M) deserialization costs when multiple subscribers are active.
            if (buffer.Length < PacketConstants.HeaderSize)
            {
                return;
            }

            ref readonly PacketHeader header = ref buffer.Span.AsHeaderRef();
            if (header.OpCode != targetOpCode)
            {
                return;
            }

            try
            {
                IPacket p = PacketRegistry.Deserialize(buffer.Span);
                p.Header = header;

                try
                {
                    if (p is TPacket t)
                    {
                        handler(t);
                    }
                }
                finally
                {
                    // The dispatcher owns packets by default; async adapters can transfer
                    // ownership to their consumers by passing disposeAfter: false.
                    if (disposeAfter && p is IDisposable d)
                    {
                        d.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                SdkLog.Error("SDK.Subscriptions", $"On<{typeof(TPacket).Name}> handler failed.", ex);
            }
        }

        client.OnMessageReceived += Wrapper;
        return new Unsub(() => client.OnMessageReceived -= Wrapper);
    }

    /// <summary>
    /// Subscribes to strongly-typed packets and throws if a different packet type is received.
    /// Use this only for debugging or for channels that are guaranteed to carry exactly one type.
    /// </summary>
    /// <typeparam name="TPacket">The packet type to receive.</typeparam>
    /// <param name="client">The transport session to subscribe to.</param>
    /// <param name="handler">The callback invoked for each received packet.</param>
    /// <returns>An <see cref="IDisposable"/> that removes the subscription when disposed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IDisposable OnExact<TPacket>(this ITransportSession client, Action<TPacket> handler)
        where TPacket : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(handler);

        void Wrapper(object? _, IBufferLease buffer)
        {
            try
            {
                ref readonly PacketHeader header = ref buffer.Span.AsHeaderRef();
                IPacket p = PacketRegistry.Deserialize(buffer.Span);
                p.Header = header;

                try
                {
                    if (p is not TPacket t)
                    {
                        SdkLog.Warn("SDK.Subscriptions", $"OnExact<{typeof(TPacket).Name}> received unexpected packet {p?.GetType().Name ?? "null"}.");
                        return;
                    }

                    handler(t);
                }
                finally
                {
                    if (p is IDisposable d)
                    {
                        d.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                SdkLog.Error("SDK.Subscriptions", $"OnExact<{typeof(TPacket).Name}> handler failed.", ex);
            }
        }

        client.OnMessageReceived += Wrapper;
        return new Unsub(() => client.OnMessageReceived -= Wrapper);
    }

    // ── On with predicate ────────────────────────────────────────────────────

    /// <summary>Subscribes to packets that match a predicate.</summary>
    /// <param name="client">The transport session to subscribe to.</param>
    /// <param name="predicate">A filter that determines whether a packet should be delivered.</param>
    /// <param name="handler">The callback invoked for each matching packet.</param>
    /// <returns>An <see cref="IDisposable"/> that removes the subscription when disposed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IDisposable On(this ITransportSession client, Func<IPacket, bool> predicate, Action<IPacket> handler)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(handler);

        void Wrapper(object? _, IBufferLease buffer)
        {
            try
            {
                ref readonly PacketHeader header = ref buffer.Span.AsHeaderRef();
                IPacket p = PacketRegistry.Deserialize(buffer.Span);
                p.Header = header;

                if (p is null || !predicate(p))
                {
                    return;
                }

                handler(p);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                SdkLog.Error("SDK.Subscriptions", "On(predicate) handler failed.", ex);
            }
        }

        client.OnMessageReceived += Wrapper;
        return new Unsub(() => client.OnMessageReceived -= Wrapper);
    }

    // ── OnOnce<TPacket> ──────────────────────────────────────────────────────

    /// <summary>
    /// One-shot subscription: auto-unsubscribes after the first matching packet.
    /// Thread-safe via <see cref="Interlocked"/>.
    /// </summary>
    /// <typeparam name="TPacket">The packet type to receive.</typeparam>
    /// <param name="client">The transport session to subscribe to.</param>
    /// <param name="predicate">A filter that determines whether a packet should be delivered.</param>
    /// <param name="handler">The callback invoked for the first matching packet.</param>
    /// <param name="disposeAfter">Whether to dispose the packet after the handler returns (default: true).</param>
    /// <returns>An <see cref="IDisposable"/> that removes the subscription when disposed.</returns>
    [Obsolete("Callback-based one-shot subscriptions are deprecated and will be removed in a future version. Use async/await Request-Response APIs instead.")]
    public static IDisposable OnOnce<TPacket>(this ITransportSession client, Func<TPacket, bool> predicate, Action<TPacket> handler, bool disposeAfter = true)
        where TPacket : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(handler);

        ushort targetOpCode = TPacket.StaticOpCode;
        int fired = 0;

        void Wrapper(object? _, IBufferLease buffer)
        {
            if (buffer.Length < PacketConstants.HeaderSize)
            {
                return;
            }

            ref readonly PacketHeader header = ref buffer.Span.AsHeaderRef();
            if (header.OpCode != targetOpCode)
            {
                return;
            }

            try
            {
                IPacket p = PacketRegistry.Deserialize(buffer.Span);
                p.Header = header;

                bool delivered = false;
                try
                {
                    if (p is not TPacket t || !predicate(t))
                    {
                        return;
                    }

                    // Atomic once-guard: only the first arriving thread proceeds.
                    if (Interlocked.Exchange(ref fired, 1) != 0)
                    {
                        return;
                    }

                    delivered = true;
                    client.OnMessageReceived -= Wrapper;
                    handler(t);
                }
                finally
                {
                    if ((!delivered || disposeAfter) && p is IDisposable d)
                    {
                        d.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                SdkLog.Error("SDK.Subscriptions", $"OnOnce<{typeof(TPacket).Name}> handler failed.", ex);
            }
        }

        client.OnMessageReceived += Wrapper;
        return new Unsub(() => client.OnMessageReceived -= Wrapper);
    }

    // ── SubscribeTemp strongly-typed ─────────────────────────────────────────

    /// <summary>
    /// Subscribes to strongly-typed packets for the duration of a scoped operation.
    /// Automatically unsubscribes when the returned <see cref="IDisposable"/> is disposed.
    /// </summary>
    /// <typeparam name="TPacket">The packet type to receive.</typeparam>
    /// <param name="client">The transport session to subscribe to.</param>
    /// <param name="onMessage">Handler invoked for each matching packet.</param>
    /// <param name="onDisconnected">Optional handler invoked when the session disconnects while the subscription is active.</param>
    /// <returns>
    /// An <see cref="IDisposable"/> that unsubscribes both handlers when disposed.
    /// Always wrap in a <c>using</c> statement.
    /// </returns>
    /// <example>
    /// <code>
    /// using var sub = client.SubscribeTemp&lt;LoginResponse&gt;(
    ///     onMessage:      resp => tcs.TrySetResult(resp),
    ///     onDisconnected: ex   => tcs.TrySetException(ex));
    ///
    /// await client.SendAsync(loginRequest, ct);
    /// var response = await tcs.Task;
    /// </code>
    /// </example>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Obsolete("Callback-based temporary subscriptions are deprecated. Use async/await Request-Response APIs instead.")]
    public static IDisposable SubscribeTemp<TPacket>(this ITransportSession client, Action<TPacket> onMessage, Action<Exception>? onDisconnected = null)
        where TPacket : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(onMessage);

        IDisposable msgSub = client.On(onMessage);

        if (onDisconnected is null)
        {
            return msgSub;
        }

        void DisconnectHandler(object? _, Exception ex)
        {
            try
            {
                onDisconnected(ex);
            }
            catch (Exception callbackEx) when (ExceptionClassifier.IsNonFatal(callbackEx))
            {
                SdkLog.Error("SDK.Subscriptions", $"SubscribeTemp<{typeof(TPacket).Name}> disconnect handler failed.", callbackEx);
            }
        }

        client.OnDisconnected += DisconnectHandler;

        return client.Subscribe(msgSub, new DelegateDisposable(() => client.OnDisconnected -= DisconnectHandler));
    }

    /// <summary>
    /// Subscribes to strongly-typed packets with a predicate filter for the duration of a scoped operation.
    /// </summary>
    /// <typeparam name="TPacket">The packet type to receive.</typeparam>
    /// <param name="client">The transport session to subscribe to.</param>
    /// <param name="predicate">A filter that determines whether a packet should be delivered.</param>
    /// <param name="onMessage">Handler invoked for each matching packet.</param>
    /// <param name="onDisconnected">Optional handler invoked when the session disconnects while the subscription is active.</param>
    /// <returns>An <see cref="IDisposable"/> that unsubscribes when disposed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Obsolete("Callback-based temporary subscriptions are deprecated. Use async/await Request-Response APIs instead.")]
    public static IDisposable SubscribeTemp<TPacket>(this ITransportSession client, Func<TPacket, bool> predicate, Action<TPacket> onMessage, Action<Exception>? onDisconnected = null)
        where TPacket : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(onMessage);

        IDisposable msgSub = client.On<TPacket>(p =>
        {
            if (predicate(p))
            {
                onMessage(p);
            }
        });

        if (onDisconnected is null)
        {
            return msgSub;
        }

        void DisconnectHandler(object? _, Exception ex)
        {
            try
            {
                onDisconnected(ex);
            }
            catch (Exception callbackEx) when (ExceptionClassifier.IsNonFatal(callbackEx))
            {
                SdkLog.Error("SDK.Subscriptions", $"SubscribeTemp<{typeof(TPacket).Name}> predicate disconnect handler failed.", callbackEx);
            }
        }

        client.OnDisconnected += DisconnectHandler;

        return client.Subscribe(msgSub, new DelegateDisposable(() => client.OnDisconnected -= DisconnectHandler));
    }

    // ── Subscribe (composite) ────────────────────────────────────────────────

    /// <summary>
    /// Groups multiple subscriptions into a single <see cref="CompositeSubscription"/>.
    /// </summary>
    /// <param name="_">The transport session used for fluent syntax.</param>
    /// <param name="subs">The subscriptions to group.</param>
    /// <returns>A composite handle that disposes all subscriptions together.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CompositeSubscription Subscribe(this ITransportSession _, params IDisposable[] subs) => new(subs);

    // ── Internal ─────────────────────────────────────────────────────────────

    private sealed class Unsub(Action dispose) : IDisposable
    {
        private Action _dispose = dispose;

        /// <inheritdoc/>
        public void Dispose() => Interlocked.Exchange(ref _dispose!, null)?.Invoke();
    }
}

/// <summary>
/// Groups multiple <see cref="IDisposable"/> subscriptions into one disposable handle.
/// Thread-safe; supports adding new subscriptions after construction.
/// </summary>
public sealed class CompositeSubscription : IDisposable
{
    private int _disposed;
    private volatile IDisposable[] _subs;

    /// <summary>
    /// Initializes a new <see cref="CompositeSubscription"/> with the specified subscriptions.
    /// </summary>
    /// <param name="subs">The subscriptions to include.</param>
    public CompositeSubscription(params IDisposable[] subs) => _subs = subs ?? [];

    /// <summary>
    /// Adds a new subscription.
    /// If already disposed, the subscription is disposed immediately.
    /// </summary>
    /// <param name="sub">The subscription to add.</param>
    public void Add(IDisposable sub)
    {
        if (sub is null)
        {
            return;
        }

        if (Volatile.Read(ref _disposed) == 1)
        {
            sub.Dispose();
            return;
        }

        while (true)
        {
            IDisposable[] current = _subs;
            IDisposable[] updated = new IDisposable[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[^1] = sub;

            if (Interlocked.CompareExchange(ref _subs, updated, current) == current)
            {
                break;
            }
        }

        // Re-check disposed after insertion to handle race between Add and Dispose.
        if (Volatile.Read(ref _disposed) == 1)
        {
            sub.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (IDisposable s in Interlocked.Exchange(ref _subs, []))
        {
            try
            {
                s?.Dispose();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                SdkLog.Warn("SDK.Subscriptions", "CompositeSubscription.Dispose: a subscription failed to dispose.", ex);
            }
        }
    }
}

/// <summary>
/// An <see cref="IDisposable"/> that executes a delegate on disposal.
/// Used internally to wrap event unsubscription delegates into a disposable handle.
/// Thread-safe: the delegate is invoked at most once.
/// </summary>
/// <param name="onDispose">The action to invoke when disposed.</param>
internal sealed class DelegateDisposable(Action onDispose) : IDisposable
{
    private Action _onDispose = onDispose;

    /// <inheritdoc/>
    public void Dispose()
        => Interlocked.Exchange(ref _onDispose!, null)?.Invoke();
}
