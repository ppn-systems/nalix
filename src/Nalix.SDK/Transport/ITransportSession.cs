// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.SDK.Options;

namespace Nalix.SDK.Transport;

/// <summary>
/// Provides the contract implemented by <see cref="TransportSession"/>, allowing
/// callers to decorate or fake a session (retry, tracing, metrics, test doubles)
/// without depending on a concrete or sealed session type.
/// </summary>
public interface ITransportSession
{
    /// <summary>
    /// Gets the transport options used by the session.
    /// </summary>
    TransportOptions Options { get; }

    /// <summary>
    /// Gets the runtime state of the session (e.g. encryption keys, session tokens).
    /// </summary>
    SessionState State { get; }

    /// <summary>
    /// Gets a value indicating whether the session is currently connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets or sets a hook invoked after an automatic reconnect performs a fresh handshake
    /// (i.e. session resume was not used), before the session is signalled as ready.
    /// </summary>
    Func<CancellationToken, Task>? OnReauthenticateAsync { get; set; }

    /// <summary>
    /// Gets or sets a hook invoked after an automatic reconnect successfully resumes an existing
    /// session, before the session is signalled as ready.
    /// </summary>
    Func<CancellationToken, Task>? OnResumedAsync { get; set; }

    /// <summary>
    /// Occurs when the session establishes a connection.
    /// </summary>
    event EventHandler? OnConnected;

    /// <summary>
    /// Occurs when the session has completed its handshake or session resume and is ready for
    /// application-level traffic. Fired on all connection paths, including automatic reconnects.
    /// </summary>
    event EventHandler? OnReady;

    /// <summary>
    /// Occurs when the session disconnects.
    /// </summary>
    event EventHandler<Exception>? OnDisconnected;

    /// <summary>
    /// Occurs when a message is received from the remote endpoint.
    /// </summary>
    event EventHandler<IBufferLease>? OnMessageReceived;

    /// <summary>
    /// Occurs when a transport-level error is encountered.
    /// </summary>
    event EventHandler<Exception>? OnError;

    /// <summary>
    /// Asynchronously connects to the configured remote endpoint.
    /// </summary>
    Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default);

    /// <summary>
    /// Asynchronously disconnects from the remote endpoint.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Asynchronously sends a packet to the remote endpoint.
    /// </summary>
    Task SendAsync(IPacket packet, CancellationToken ct = default);

    /// <summary>
    /// Asynchronously sends a packet to the remote endpoint, overriding the default encryption behavior.
    /// </summary>
    Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default);

    /// <summary>
    /// Asynchronously sends raw binary data to the remote endpoint.
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default);

    /// <summary>
    /// Resets the sequence counters for both send and receive directions.
    /// </summary>
    void ResetSequenceCounters();

    /// <summary>
    /// Awaits until the session is connected and, if a fresh handshake occurred, re-authenticated.
    /// Completes immediately if already connected/no reconnect in flight, or if auto-reconnect is
    /// not enabled.
    /// </summary>
    Task WaitUntilReadyAsync(CancellationToken ct = default);
}
