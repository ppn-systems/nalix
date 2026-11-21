// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Provides Reactive Extensions (Rx) integration for <see cref="TransportSession"/> using <see cref="IObservable{T}"/>.
/// </summary>
public static class ObservableExtensions
{
    /// <summary>
    /// Creates an observable sequence from the transport session for the specified packet type.
    /// Uses <see cref="TransportSessionSubscriptions.On{TPacket}(ITransportSession, Action{TPacket}, bool)"/> under the hood.
    /// </summary>
    /// <typeparam name="TEvent">The packet type to observe.</typeparam>
    /// <param name="client">The transport session.</param>
    /// <returns>An observable sequence of packets.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="client"/> is null.</exception>
    public static IObservable<TEvent> AsObservable<TEvent>(this ITransportSession client)
        where TEvent : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        return new PacketObservable<TEvent>(client);
    }

    private sealed class PacketObservable<TEvent>(ITransportSession client) : IObservable<TEvent>
        where TEvent : class, IPacket, IPacketStaticOpcode
    {
        public IDisposable Subscribe(IObserver<TEvent> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);

            // 1. Subscribe to the event stream
#pragma warning disable CA2000 // Ownership is transferred to the Unsubscriber
            IDisposable msgSub = client.On<TEvent>(packet =>
            {
                try
                {
                    observer.OnNext(packet);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    // Exceptions thrown by the observer's OnNext should propagate to OnError per Rx contract
                    observer.OnError(ex);
                }
            });
#pragma warning restore CA2000

            // 2. Handle disconnections
            void OnDisconnected(object? sender, Exception ex)
            {
                if (ex != null)
                {
                    observer.OnError(ex);
                }
                else
                {
                    observer.OnCompleted();
                }
            }

            client.OnDisconnected += OnDisconnected;

            // 3. Return the cleanup handle
            return new Unsubscriber(() =>
            {
                msgSub.Dispose();
                client.OnDisconnected -= OnDisconnected;
            });
        }
    }

    private sealed class Unsubscriber(Action disposeAction) : IDisposable
    {
        private Action? _disposeAction = disposeAction;

        public void Dispose() => Interlocked.Exchange(ref _disposeAction, null)?.Invoke();
    }
}
