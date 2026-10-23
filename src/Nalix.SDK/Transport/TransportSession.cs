// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Environment.Extensions;
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
    protected TransportSession(SessionState? state = null)
    {
        this.State = state ?? new SessionState();
        this.ReconnectSupervisor = new Internal.ReconnectSupervisor(this);
    }

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

    /// <summary>
    /// Gets or sets a hook invoked after an automatic reconnect successfully resumes an existing
    /// session, before the session is signalled as ready.
    /// </summary>
    public Func<CancellationToken, Task>? OnResumedAsync { get; set; }

    private int _intentionalDisconnect;
    private int _suppressDisconnectCount;
    private int _deferReadyCount;
    private int _negotiationDepth;
    private int _outboundSeq;

    private readonly Lock _keepAliveLock = new();
#pragma warning disable CA2213
    private CancellationTokenSource? _keepAliveCts;
#pragma warning restore CA2213

    private readonly Lock _pendingRequestsLock = new();
    private Internal.IPendingRequestObserver?[] _pendingRequests = new Internal.IPendingRequestObserver?[8];
    private int _pendingRequestsCount;

    /// <summary>
    /// Gets a value indicating whether this session dispatches inbound frames and disconnect
    /// notifications directly to the internal pending-request table without allocating multicast
    /// event delegates.
    /// </summary>
    internal virtual bool HasDirectPendingDispatch => false;

    internal void RegisterPendingRequest(Internal.IPendingRequestObserver observer)
    {
        lock (_pendingRequestsLock)
        {
            Internal.IPendingRequestObserver?[] slots = _pendingRequests;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] is null)
                {
                    slots[i] = observer;
                    _ = Interlocked.Increment(ref _pendingRequestsCount);
                    return;
                }
            }

            int oldLen = slots.Length;
            Internal.IPendingRequestObserver?[] expanded = new Internal.IPendingRequestObserver?[oldLen * 2];
            Array.Copy(slots, expanded, oldLen);
            expanded[oldLen] = observer;
            _pendingRequests = expanded;
            _ = Interlocked.Increment(ref _pendingRequestsCount);
        }
    }

    internal void UnregisterPendingRequest(Internal.IPendingRequestObserver observer)
    {
        if (Volatile.Read(ref _pendingRequestsCount) == 0)
        {
            return;
        }

        lock (_pendingRequestsLock)
        {
            Internal.IPendingRequestObserver?[] slots = _pendingRequests;
            for (int i = 0; i < slots.Length; i++)
            {
                if (ReferenceEquals(slots[i], observer))
                {
                    slots[i] = null;
                    _ = Interlocked.Decrement(ref _pendingRequestsCount);
                    return;
                }
            }
        }
    }

    internal void DispatchPendingMessage([Borrowed] IBufferLease lease)
    {
        if (Volatile.Read(ref _pendingRequestsCount) == 0 || lease.Length < PacketConstants.HeaderSize)
        {
            return;
        }

        ushort opCode = lease.Span.AsHeaderRef().OpCode;

        Internal.IPendingRequestObserver? obs0 = null;
        Internal.IPendingRequestObserver? obs1 = null;
        Internal.IPendingRequestObserver? obs2 = null;
        Internal.IPendingRequestObserver? obs3 = null;
        Internal.IPendingRequestObserver[]? overflow = null;
        int matchCount = 0;

        lock (_pendingRequestsLock)
        {
            if (_pendingRequestsCount == 0)
            {
                return;
            }

            Internal.IPendingRequestObserver?[] slots = _pendingRequests;
            for (int i = 0; i < slots.Length; i++)
            {
                Internal.IPendingRequestObserver? obs = slots[i];
                if (obs is null || obs.OpCode != opCode)
                {
                    continue;
                }

                switch (matchCount)
                {
                    case 0:
                        obs0 = obs;
                        break;
                    case 1:
                        obs1 = obs;
                        break;
                    case 2:
                        obs2 = obs;
                        break;
                    case 3:
                        obs3 = obs;
                        break;
                    default:
                        overflow ??= ArrayPool<Internal.IPendingRequestObserver>.Shared.Rent(slots.Length);
                        overflow[matchCount - 4] = obs;
                        break;
                }

                matchCount++;
            }
        }

        if (matchCount == 0)
        {
            return;
        }

        obs0!.OnMessageReceived(lease);
        if (matchCount > 1)
        {
            obs1!.OnMessageReceived(lease);
        }
        if (matchCount > 2)
        {
            obs2!.OnMessageReceived(lease);
        }
        if (matchCount > 3)
        {
            obs3!.OnMessageReceived(lease);
        }

        if (overflow is not null)
        {
            int extraCount = matchCount - 4;
            try
            {
                for (int i = 0; i < extraCount; i++)
                {
                    overflow[i].OnMessageReceived(lease);
                }
            }
            finally
            {
                Array.Clear(overflow, 0, extraCount);
                ArrayPool<Internal.IPendingRequestObserver>.Shared.Return(overflow);
            }
        }
    }

    internal void DispatchPendingDisconnect(Exception error)
    {
        if (Volatile.Read(ref _pendingRequestsCount) == 0)
        {
            return;
        }

        Internal.IPendingRequestObserver? obs0 = null;
        Internal.IPendingRequestObserver? obs1 = null;
        Internal.IPendingRequestObserver? obs2 = null;
        Internal.IPendingRequestObserver? obs3 = null;
        Internal.IPendingRequestObserver[]? overflow = null;
        int count = 0;

        lock (_pendingRequestsLock)
        {
            if (_pendingRequestsCount == 0)
            {
                return;
            }

            Internal.IPendingRequestObserver?[] slots = _pendingRequests;
            for (int i = 0; i < slots.Length; i++)
            {
                Internal.IPendingRequestObserver? obs = slots[i];
                if (obs is null)
                {
                    continue;
                }

                slots[i] = null;
                switch (count)
                {
                    case 0:
                        obs0 = obs;
                        break;
                    case 1:
                        obs1 = obs;
                        break;
                    case 2:
                        obs2 = obs;
                        break;
                    case 3:
                        obs3 = obs;
                        break;
                    default:
                        overflow ??= ArrayPool<Internal.IPendingRequestObserver>.Shared.Rent(slots.Length);
                        overflow[count - 4] = obs;
                        break;
                }

                count++;
            }

            _ = Interlocked.Exchange(ref _pendingRequestsCount, 0);
        }

        if (count == 0)
        {
            return;
        }

        obs0!.OnDisconnected(error);
        if (count > 1)
        {
            obs1!.OnDisconnected(error);
        }
        if (count > 2)
        {
            obs2!.OnDisconnected(error);
        }
        if (count > 3)
        {
            obs3!.OnDisconnected(error);
        }

        if (overflow is not null)
        {
            int extraCount = count - 4;
            try
            {
                for (int i = 0; i < extraCount; i++)
                {
                    overflow[i].OnDisconnected(error);
                }
            }
            finally
            {
                Array.Clear(overflow, 0, extraCount);
                ArrayPool<Internal.IPendingRequestObserver>.Shared.Return(overflow);
            }
        }
    }

    /// <summary>
    /// Gets the reconnect supervisor for this session. Always created in the constructor so
    /// <see cref="OnDisconnected"/> is subscribed from the start and <see cref="WaitUntilReadyAsync"/>
    /// observes any in-flight reconnect even if <see cref="Options.TransportOptions.AutoReconnectEnabled"/>
    /// is toggled dynamically.
    /// </summary>
    internal Internal.ReconnectSupervisor ReconnectSupervisor { get; }

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
    /// Gets a value indicating whether public <see cref="OnDisconnected"/> notifications are currently
    /// suppressed during an internal transition (such as resume-to-handshake fallback).
    /// </summary>
    internal bool IsDisconnectSuppressed => Volatile.Read(ref _suppressDisconnectCount) > 0;

    internal void EnterSuppressDisconnect() => Interlocked.Increment(ref _suppressDisconnectCount);

    internal void ExitSuppressDisconnect() => Interlocked.Decrement(ref _suppressDisconnectCount);

    internal void EnterDeferReady() => Interlocked.Increment(ref _deferReadyCount);

    internal void ExitDeferReady() => Interlocked.Decrement(ref _deferReadyCount);

    internal void EnterNegotiation() => Interlocked.Increment(ref _negotiationDepth);

    internal void ExitNegotiation() => Interlocked.Decrement(ref _negotiationDepth);

    /// <summary>
    /// Signals that the session has completed its handshake or session resume and is ready for
    /// application-level traffic. Also starts/refreshes the keepalive monitor when configured.
    /// </summary>
    internal void NotifyReady()
    {
        this.StartKeepAliveIfEnabled();

        if (Volatile.Read(ref _deferReadyCount) == 0)
        {
            this.OnReady?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Reconnects the underlying socket during a failed-resume fallback without invoking the
    /// public <see cref="DisconnectAsync"/> path or emitting spurious <see cref="OnDisconnected"/>
    /// / duplicate <see cref="OnConnected"/> events.
    /// </summary>
    internal virtual Task ReconnectForResumeFallbackAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
        => this.ConnectAsync(host, port, ct);

    internal void StartKeepAliveIfEnabled()
    {
        if (this.Options.KeepAliveIntervalMillis <= 0 || !this.IsConnected)
        {
            return;
        }

        CancellationTokenSource? previous;
        CancellationTokenSource next = new();
        lock (_keepAliveLock)
        {
            previous = _keepAliveCts;
            _keepAliveCts = next;
        }

        if (previous is not null)
        {
            try
            {
#pragma warning disable CA1849
                previous.Cancel();
#pragma warning restore CA1849
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                previous.Dispose();
            }
        }

        _ = this.KeepAliveLoopAsync(next);
    }

    internal void StopKeepAlive()
    {
        CancellationTokenSource? previous;
        lock (_keepAliveLock)
        {
            previous = _keepAliveCts;
            _keepAliveCts = null;
        }

        if (previous is not null)
        {
            try
            {
#pragma warning disable CA1849
                previous.Cancel();
#pragma warning restore CA1849
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                previous.Dispose();
            }
        }
    }

    private async Task KeepAliveLoopAsync(CancellationTokenSource cts)
    {
        CancellationToken ct = cts.Token;
        int consecutiveMisses = 0;

        try
        {
            while (!ct.IsCancellationRequested && this.IsConnected)
            {
                int intervalMs = this.Options.KeepAliveIntervalMillis;
                if (intervalMs <= 0)
                {
                    break;
                }

                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || !this.IsConnected)
                {
                    break;
                }

                if (Volatile.Read(ref _negotiationDepth) > 0)
                {
                    continue;
                }

                try
                {
                    int timeoutMs = Math.Max(100, this.Options.KeepAliveTimeoutMillis);
                    _ = await Extensions.PingExtensions.PingAsync(this, timeoutMs, ct).ConfigureAwait(false);
                    consecutiveMisses = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (Nalix.Abstractions.Exceptions.ExceptionClassifier.IsNonFatal(ex))
                {
                    consecutiveMisses++;
                    int threshold = Math.Max(1, this.Options.KeepAliveMissThreshold);
                    if (consecutiveMisses >= threshold && !ct.IsCancellationRequested && this.IsConnected)
                    {
                        await this.FaultFromKeepAliveAsync(
                            new Nalix.Abstractions.Exceptions.NetworkException(
                                $"Keepalive failed after {consecutiveMisses} missed PONG response(s): {ex.Message}", ex)).ConfigureAwait(false);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Tears down the active transport as an unexpected fault when keepalive probes fail,
    /// allowing <see cref="ReconnectSupervisor"/> to recover the session.
    /// </summary>
    private protected virtual Task FaultFromKeepAliveAsync(Exception ex) => Task.CompletedTask;

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
    /// Completes immediately if the session is already connected and no reconnect is in flight.
    /// </summary>
    /// <param name="ct">The token to observe while waiting.</param>
    /// <exception cref="TimeoutException">Auto-reconnect exhausted its configured attempts.</exception>
    public Task WaitUntilReadyAsync(CancellationToken ct = default)
        => this.ReconnectSupervisor.ReadyAsync(ct);

    #endregion Properties

    #region Events

    /// <summary>
    /// Occurs when the session establishes a connection.
    /// </summary>
    public abstract event EventHandler? OnConnected;

    /// <summary>
    /// Occurs when the session has completed its handshake or session resume and is ready for
    /// application-level traffic. Fired on all connection paths, including automatic reconnects.
    /// </summary>
    public event EventHandler? OnReady;

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
