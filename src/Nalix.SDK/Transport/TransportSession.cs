// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.SDK.Options;

namespace Nalix.SDK.Transport;

/// <summary>
/// Provides the Abstractions contract for client-side transport sessions.
/// </summary>
/// <remarks>
/// Derived sessions expose a consistent lifecycle for connecting, disconnecting,
/// sending packets, and receiving transport events.
/// </remarks>
public abstract class TransportSession : IDisposable, ITransportSession
{
    #region Properties

    /// <summary>
    /// Gets the transport options used by the session.
    /// </summary>
    public abstract TransportOptions Options { get; }

    /// <summary>
    /// Gets the runtime state of the session (e.g. encryption keys, session tokens).
    /// </summary>
    public SessionState State { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportSession"/> class.
    /// </summary>
    /// <param name="state">An optional shared state instance. If null, a new instance is created.</param>
    protected TransportSession(SessionState? state = null) => this.State = state ?? new SessionState();

    /// <summary>
    /// Gets a value indicating whether the session is currently connected.
    /// </summary>
    public abstract bool IsConnected { get; }

    /// <summary>
    /// Gets or sets a hook invoked after an automatic reconnect performs a fresh handshake
    /// (i.e. session resume was not used), before the session is signalled as ready.
    /// Lets the application re-establish identity without reimplementing handshake ordering.
    /// Only used when <see cref="Options.TransportOptions.AutoReconnectEnabled"/> is set.
    /// </summary>
    public Func<CancellationToken, Task>? OnReauthenticateAsync { get; set; }

    private Internal.ReconnectSupervisor? _reconnectSupervisor;
    private int _intentionalDisconnect;
    private int _outboundSeq;

    /// <summary>
    /// Gets the reconnect supervisor for this session, creating it lazily when
    /// <see cref="Options.TransportOptions.AutoReconnectEnabled"/> is enabled. Returns
    /// <see langword="null"/> when auto-reconnect is disabled.
    /// </summary>
    internal Internal.ReconnectSupervisor? ReconnectSupervisor
    {
        get
        {
            if (!this.Options.AutoReconnectEnabled)
            {
                return null;
            }

            return _reconnectSupervisor ??= new Internal.ReconnectSupervisor(this);
        }
    }

    /// <summary>
    /// Marks the next <see cref="EventHandler{Exception}"/> raise of <c>OnDisconnected</c> as
    /// application-initiated, so <see cref="ReconnectSupervisor"/> skips auto-reconnect for it.
    /// Called by concrete sessions from their public <c>DisconnectAsync()</c> entry point only —
    /// never from internal fault-handling paths.
    /// </summary>
    internal void MarkIntentionalDisconnect() => Interlocked.Exchange(ref _intentionalDisconnect, 1);

    /// <summary>
    /// Consumes (reads and clears) the intentional-disconnect flag. Used by
    /// <see cref="Internal.ReconnectSupervisor"/> to decide whether an <c>OnDisconnected</c>
    /// raise should start a reconnect loop.
    /// </summary>
    internal bool ConsumeIntentionalDisconnect() => Interlocked.Exchange(ref _intentionalDisconnect, 0) == 1;

    /// <summary>
    /// Assigns an auto-incrementing <c>SequenceId</c> to <paramref name="packet"/> when the caller
    /// left it unset (<c>0</c>). Packets with a caller-assigned SequenceId are never overwritten.
    /// Called by concrete sessions from their <c>SendAsync(IPacket, ...)</c> override, before serialization,
    /// and by <see cref="Extensions.StreamExtensions"/> so it can latch the same value it will send with
    /// before subscribing, instead of racing the stamp that <c>SendAsync</c> would otherwise apply later.
    /// Internal rather than private protected so extension code in this assembly can call it directly on
    /// a concrete session without widening the public API.
    /// </summary>
    internal void StampSequenceIdIfUnset(IPacket packet)
    {
        if (packet.Header.SequenceId != 0)
        {
            return;
        }

        PacketHeader header = packet.Header;
        header.SequenceId = unchecked((ushort)Interlocked.Increment(ref _outboundSeq));
        packet.Header = header;
    }

    /// <summary>
    /// Awaits until the session is connected and, if a fresh handshake occurred, re-authenticated.
    /// Completes immediately if the session is already connected and no reconnect is in flight, or
    /// if <see cref="Options.TransportOptions.AutoReconnectEnabled"/> is not set.
    /// </summary>
    /// <param name="ct">The token to observe while waiting.</param>
    /// <exception cref="TimeoutException">Auto-reconnect exhausted its configured attempts.</exception>
    public Task WaitUntilReadyAsync(CancellationToken ct = default)
        => this.ReconnectSupervisor?.ReadyAsync(ct) ?? Task.CompletedTask;

    #endregion Properties

    #region Events

    /// <summary>
    /// Occurs when the session establishes a connection.
    /// </summary>
    public abstract event EventHandler? OnConnected;

    /// <summary>
    /// Occurs when the session disconnects.
    /// </summary>
    public abstract event EventHandler<Exception>? OnDisconnected;

    /// <summary>
    /// Occurs when a message is received from the remote endpoint.
    /// The lease is owned by the transport and will be disposed after the event returns;
    /// handlers must call <see cref="IBufferLease.Retain"/> if they need to keep it.
    /// </summary>
    public abstract event EventHandler<IBufferLease>? OnMessageReceived;

    /// <summary>
    /// Occurs when a transport-level error is encountered.
    /// </summary>
    public abstract event EventHandler<Exception>? OnError;

    #endregion Events

    #region Methods

    /// <summary>
    /// Asynchronously connects to the configured remote endpoint.
    /// </summary>
    /// <param name="host">The target host name or address. If <see langword="null"/> or empty, the configured default is used.</param>
    /// <param name="port">The target port. If <see langword="null"/>, the configured default is used.</param>
    /// <param name="ct">The token to observe while connecting.</param>
    public abstract Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default);

    /// <summary>
    /// Asynchronously disconnects from the remote endpoint.
    /// </summary>
    public abstract Task DisconnectAsync();

    /// <summary>
    /// Asynchronously sends a packet to the remote endpoint.
    /// </summary>
    /// <param name="packet">The packet to send.</param>
    /// <param name="ct">The token to observe while sending.</param>
    public abstract Task SendAsync(IPacket packet, CancellationToken ct = default);

    /// <summary>
    /// Asynchronously sends raw binary data to the remote endpoint.
    /// </summary>
    /// <param name="packet">The packet to send.</param>
    /// <param name="encrypt">A value that overrides the default encryption behavior.</param>
    /// <param name="ct">The token to observe while sending.</param>
    public abstract Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default);

    /// <summary>
    /// Asynchronously sends raw binary data to the remote endpoint.
    /// </summary>
    /// <param name="payload">The payload to send.</param>
    /// <param name="encrypt">A value that overrides the default encryption behavior.</param>
    /// <param name="ct">The token to observe while sending.</param>
    public abstract Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default);

    /// <summary>
    /// Resets the sequence counters for both send and receive directions.
    /// This should be called immediately after a successful key rotation (e.g., SessionRekey).
    /// </summary>
    public abstract void ResetSequenceCounters();

    /// <summary>
    /// Releases all resources used by the session.
    /// </summary>
    public void Dispose()
    {
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases managed and unmanaged resources used by the session.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected abstract void Dispose(bool disposing);

    #endregion Methods
}
