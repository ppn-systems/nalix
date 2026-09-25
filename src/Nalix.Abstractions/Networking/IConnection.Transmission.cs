// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nalix.Abstractions.Networking;

public partial interface IConnection
{
    /// <summary>
    /// Gets the Transmission CONTROL number (TCP) transmission interface.
    /// For socket-based connections, cast to <see cref="ISocketTransport"/> for direct socket access.
    /// </summary>
    ITransport TCP { get; }

    /// <summary>
    /// Gets the UDP companion transport associated with this TCP-backed connection.
    /// </summary>
    /// <remarks>
    /// UDP transport cannot be created as a standalone connection in this model.
    /// It is initialized from the accepted TCP connection endpoint and is intended
    /// only for datagram replies or mixed TCP/UDP protocols.
    /// </remarks>
    ITransport? UDP { get; }

    /// <summary>
    /// Represents a transport interface for sending data packets.
    /// </summary>
    interface ITransport : ITransportSequencer
    {
        /// <summary>
        /// Gets the framing strategy used by this protocol.
        /// </summary>
        TransportFraming Framing { get; }

        /// <summary>
        /// Sends a message synchronously over the connection.
        /// </summary>
        /// <param name="message">The message to send.</param>
        void Send(ReadOnlySpan<byte> message);

        /// <summary>
        /// Sends a message asynchronously over the connection.
        /// </summary>
        /// <param name="message">The data to send.</param>
        /// <param name="cancellationToken">A token to cancel the sending operation.</param>
        /// <returns>A task that represents the asynchronous sending operation.</returns>
        /// <remarks>
        /// If the connection has been authenticated, the data will be encrypted before sending.
        /// </remarks>
        ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts receiving data from the connection.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token to cancel the receiving operation.
        /// </param>
        /// <remarks>
        /// Call this method to initiate listening for incoming data on the connection.
        /// </remarks>
        void BeginReceive(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets the framing mode for this transport.
        /// Must be called before <see cref="BeginReceive"/> is invoked.
        /// </summary>
        /// <param name="framing">The framing mode to use.</param>
        void UseFraming(TransportFraming framing);

        /// <summary>
        /// Acquires a scope that must be held across sequence-number reservation
        /// (<see cref="ITransportSequencer.NextSendSequence"/>) and the corresponding wire write
        /// (<see cref="SendAsyncCore(ReadOnlyMemory{byte}, CancellationToken)"/>), so that reservation order and wire-write order stay consistent
        /// under concurrent sends.
        /// </summary>
        /// <remarks>
        /// Transports whose <see cref="SendAsync"/> already guarantees reservation/write ordering may
        /// return a no-op scope. Transports with fully asynchronous writes on ordered streams (for example,
        /// TCP and WebSocket) must return a scope backed by a real lock.
        /// </remarks>
        /// <param name="cancellationToken">A token to cancel the acquisition.</param>
        ValueTask<IAsyncDisposable> AcquireSendLockAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(Networking.NoOpAsyncDisposable.Instance);

        /// <summary>
        /// Writes <paramref name="message"/> to the transport without acquiring any additional send lock.
        /// Callers that need atomicity with sequence reservation must first acquire the scope returned by
        /// <see cref="AcquireSendLockAsync"/>.
        /// </summary>
        /// <param name="message">The data to send.</param>
        /// <param name="cancellationToken">A token to cancel the sending operation.</param>
        ValueTask SendAsyncCore(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            this.SendAsync(message, cancellationToken);

        /// <summary>
        /// Writes the payload of <paramref name="frame"/> to the transport without acquiring any additional
        /// send lock. Same contract as <see cref="SendAsyncCore(ReadOnlyMemory{byte}, CancellationToken)"/>,
        /// but a transport may use space the lease reserved in front of the payload to write its own framing
        /// header in place instead of copying the frame. The caller keeps ownership of <paramref name="frame"/>
        /// and must keep it alive until the returned task completes.
        /// </summary>
        /// <param name="frame">The frame to send.</param>
        /// <param name="cancellationToken">A token to cancel the sending operation.</param>
        ValueTask SendAsyncCore(IBufferLease frame, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(frame);
            return this.SendAsyncCore(frame.Memory, cancellationToken);
        }
    }

    /// <summary>
    /// Extends <see cref="ITransport"/> with direct access to the underlying OS socket.
    /// Only applicable to socket-based transports (e.g., TCP).
    /// </summary>
    interface ISocketTransport : ITransport
    {
        /// <summary>
        /// Gets the underlying <see cref="System.Net.Sockets.Socket"/> used by this transport.
        /// </summary>
        System.Net.Sockets.Socket Socket { get; }

        /// <summary>
        /// Gets the task representing the receive loop.
        /// Used by unwrapped connections to await loop shutdown and recover stolen packets.
        /// </summary>
        Task? ReceiveLoopTask { get; }

        /// <summary>
        /// Contains bytes that were read from the kernel but not processed by the framework
        /// due to <see cref="Unwrap"/> or cancellation.
        /// </summary>
        byte[]? StolenData { get; }

        /// <summary>
        /// Detaches the underlying OS socket from the transport engine.
        /// This allows the caller to take ownership of the socket without it being disposed by the connection.
        /// </summary>
        /// <returns>The detached <see cref="System.Net.Sockets.Socket"/>.</returns>
        System.Net.Sockets.Socket Unwrap();
    }
}

/// <summary>
/// A no-op <see cref="IAsyncDisposable"/> used as the default <see cref="IConnection.ITransport.AcquireSendLockAsync"/>
/// result for transports that need no additional locking.
/// </summary>
file sealed class NoOpAsyncDisposable : IAsyncDisposable
{
    internal static readonly NoOpAsyncDisposable Instance = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
