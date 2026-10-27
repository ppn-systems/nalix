// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Extensions;

namespace Nalix.SDK.Transport.Internal;

/// <summary>
/// Non-generic observer contract used by <see cref="TransportSession"/>'s internal pending-request
/// dispatch table to avoid multicast event delegate allocations on the request-response hot path.
/// </summary>
internal interface IPendingRequestObserver
{
    /// <summary>Gets the expected wire opcode for this pending request.</summary>
    ushort OpCode { get; }

    /// <summary>Invoked synchronously by the session when an inbound frame with matching opcode arrives.</summary>
    void OnMessageReceived([Borrowed] IBufferLease buffer);

    /// <summary>Invoked synchronously by the session when the underlying transport disconnects.</summary>
    void OnDisconnected(Exception error);
}

/// <summary>
/// One in-flight request-response exchange: it watches the session's inbound stream for the first
/// packet of <typeparamref name="TPkt"/> that satisfies a predicate, and completes when that packet
/// arrives, when the session drops, or when the caller's timeout expires.
/// </summary>
/// <typeparam name="TPkt">The response packet type.</typeparam>
#pragma warning disable CA1001 // PendingRequest<TPkt> is a process-lifetime pooled object whose Timer is reused across requests.
internal sealed class PendingRequest<TPkt> : IValueTaskSource<TPkt>, IPendingRequestObserver
    where TPkt : class, IPacket, IPacketStaticOpcode
{
    private const int MaxPooled = 64;

    private static readonly ConcurrentQueue<PendingRequest<TPkt>> s_pool = new();
    private static readonly TimerCallback s_timeoutCallback = static state => ((PendingRequest<TPkt>)state!).OnTimeout();
    private static readonly Action<object?> s_cancelCallback = static state => ((PendingRequest<TPkt>)state!).OnCanceled();

    private ManualResetValueTaskSourceCore<TPkt> _core;
    private readonly Timer _timeoutTimer;
    private CancellationTokenRegistration _ctRegistration;
    private CancellationToken _ct;
    private int _timeoutMs;

    private ITransportSession? _session;
    private TransportSession? _directSession;
    private Func<TPkt, bool>? _predicate;
    private ushort _opcode;

    // Fallback delegates used only when the session is a custom/mock ITransportSession that does not
    // participate in TransportSession's direct pending-request table.
    private readonly EventHandler<IBufferLease> _onMessage;
    private readonly EventHandler<Exception> _onDisconnected;

    private int _completed;
    private int _subscribed;

    // Bumped on every Rent and Return so callbacks that began for a previous use of this pooled
    // instance can tell they are stale.
    private int _generation;

    // Environment.TickCount64 at which the current use times out; 0 when no timer is armed. A stale
    // timer callback (queued before Return, run after a re-Rent) fires before the new deadline.
    private long _deadline;

    private PendingRequest()
    {
        _core.RunContinuationsAsynchronously = true;
        _timeoutTimer = new Timer(s_timeoutCallback, this, Timeout.Infinite, Timeout.Infinite);
        _onMessage = this.OnMessageReceivedEvent;
        _onDisconnected = this.OnDisconnectedEvent;
    }

    /// <inheritdoc/>
    public ushort OpCode => _opcode;

    /// <summary>
    /// Rents a pooled instance (or allocates one if the pool is empty) and binds it to
    /// <paramref name="session"/> and <paramref name="predicate"/> for one request.
    /// </summary>
    internal static PendingRequest<TPkt> Rent(ITransportSession session, Func<TPkt, bool> predicate)
    {
        if (!s_pool.TryDequeue(out PendingRequest<TPkt>? pending))
        {
            pending = new PendingRequest<TPkt>();
        }

        pending._deadline = 0;
        _ = Interlocked.Increment(ref pending._generation);
        pending._session = session;
        pending._directSession = session is TransportSession { HasDirectPendingDispatch: true } direct ? direct : null;
        pending._predicate = predicate;
        pending._opcode = TPkt.StaticOpCode;
        pending._completed = 0;
        pending._subscribed = 0;
        pending._timeoutMs = 0;
        pending._ct = default;

        return pending;
    }

    /// <summary>
    /// Clears per-request state, resets the <see cref="ManualResetValueTaskSourceCore{TResult}"/>,
    /// and returns the instance to the pool for reuse.
    /// </summary>
    internal void Return(bool wasAwaited)
    {
        _ = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _deadline, 0);
        this.Unsubscribe();
        _ = _timeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _ctRegistration.Dispose();
        _ctRegistration = default;

        if (!wasAwaited)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _session = null;
                _directSession = null;
                _predicate = null;
                _ct = default;

                if (s_pool.Count < MaxPooled)
                {
                    s_pool.Enqueue(this);
                }

                return;
            }

            if (_core.GetStatus(_core.Version) == ValueTaskSourceStatus.Succeeded)
            {
                TPkt unclaimed = _core.GetResult(_core.Version);
                if (unclaimed is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        _core.Reset();
        _session = null;
        _directSession = null;
        _predicate = null;
        _ct = default;

        if (s_pool.Count < MaxPooled)
        {
            s_pool.Enqueue(this);
        }
    }

    /// <summary>Starts watching the session's inbound stream.</summary>
    internal void Subscribe()
    {
        _subscribed = 1;
        if (_directSession is not null)
        {
            _directSession.RegisterPendingRequest(this);
        }
        else
        {
            _session!.OnMessageReceived += _onMessage;
            _session.OnDisconnected += _onDisconnected;
        }
    }

    /// <summary>Stops watching. Safe to call more than once.</summary>
    internal void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 0)
        {
            return;
        }

        if (_directSession is not null)
        {
            _directSession.UnregisterPendingRequest(this);
        }
        else if (_session is not null)
        {
            _session.OnMessageReceived -= _onMessage;
            _session.OnDisconnected -= _onDisconnected;
        }
    }

    /// <summary>
    /// Arms timeout and cancellation (when not already settled) and returns a zero-allocation
    /// <see cref="ValueTask{TPkt}"/> backed directly by this pooled instance.
    /// </summary>
    internal ValueTask<TPkt> WaitAsync(int timeoutMs, CancellationToken ct)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return new ValueTask<TPkt>(this, _core.Version);
        }

        _timeoutMs = timeoutMs;
        _ct = ct;

        if (ct.CanBeCanceled)
        {
            if (ct.IsCancellationRequested)
            {
                this.OnCanceled();
                return new ValueTask<TPkt>(this, _core.Version);
            }

            _ctRegistration = ct.UnsafeRegister(s_cancelCallback, this);
        }

        if (timeoutMs > 0 && Volatile.Read(ref _completed) == 0)
        {
            Volatile.Write(ref _deadline, System.Environment.TickCount64 + timeoutMs);
            _ = _timeoutTimer.Change(timeoutMs, Timeout.Infinite);
        }

        return new ValueTask<TPkt>(this, _core.Version);
    }

    /// <summary>Faults the exchange, unless it has already settled.</summary>
    internal void TrySetException(Exception error)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _ = _timeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
            this.Unsubscribe();
            _core.SetException(error);
        }
    }

    private void OnTimeout()
    {
        // Timer.Change(Infinite) cannot cancel a callback that is already queued, so this may run for a
        // previous use of the pooled instance (deadline not reached yet) or after the request settled
        // (deadline cleared). A callback that fires early relative to our clock (timer granularity differs
        // between platforms) must never lose the timeout, so re-arm for the remaining time instead of dropping it.
        long deadline = Volatile.Read(ref _deadline);
        if (deadline == 0)
        {
            return;
        }

        long remaining = deadline - System.Environment.TickCount64;
        if (remaining > 0)
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                _ = _timeoutTimer.Change(remaining, Timeout.Infinite);
            }

            return;
        }

        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            int timeoutMs = _timeoutMs;
            this.Unsubscribe();
            _core.SetException(new TimeoutException($"No {typeof(TPkt).Name} received within {timeoutMs} ms."));
        }
    }

    private void OnCanceled()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            CancellationToken ct = _ct;
            _ = _timeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
            this.Unsubscribe();
            _core.SetException(new OperationCanceledException(ct));
        }
    }

    /// <inheritdoc/>
    public void OnMessageReceived([Borrowed] IBufferLease buffer)
    {
        // The session owns the lease and disposes it after this returns; never dispose it here.
        if (buffer.Length < PacketConstants.HeaderSize)
        {
            return;
        }

        // The session invokes observers outside its lock, so this request may be returned and re-rented while
        // the predicate runs. Remember which use of the instance this frame was dispatched to.
        int generation = Volatile.Read(ref _generation);

        ref readonly PacketHeader header = ref buffer.Span.AsHeaderRef();
        if (header.OpCode != _opcode)
        {
            return;
        }

        try
        {
            IPacket packet = PacketRegistry.Deserialize(buffer.Span);
            packet.Header = header;

            bool delivered = false;

            try
            {
                if (packet is not TPkt typed)
                {
                    return;
                }

                bool matches;

                try
                {
                    matches = _predicate!(typed);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    this.TrySetException(ex);
                    return;
                }

                if (!matches)
                {
                    return;
                }

                // Only the first arriving thread hands the packet to the caller; the caller then
                // owns it, which is why it is not disposed on this path.
                if (Volatile.Read(ref _generation) != generation || Interlocked.Exchange(ref _completed, 1) != 0)
                {
                    return;
                }

                delivered = true;
                _ = _timeoutTimer.Change(Timeout.Infinite, Timeout.Infinite);
                this.Unsubscribe();

                _core.SetResult(typed);
            }
            finally
            {
                if (!delivered && packet is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            Trace.TraceError(
                "Nalix.SDK.PendingRequest<{0}> failed to handle an inbound frame: {1}",
                typeof(TPkt).Name,
                ex);
        }
    }

    /// <inheritdoc/>
    public void OnDisconnected(Exception error)
        => this.TrySetException(new NetworkException(
            $"Disconnected while waiting for {typeof(TPkt).Name}.",
            error ?? new InvalidOperationException("The transport session was disconnected.")));

    private void OnMessageReceivedEvent(object? sender, [Borrowed] IBufferLease buffer)
        => this.OnMessageReceived(buffer);

    private void OnDisconnectedEvent(object? sender, Exception error)
        => this.OnDisconnected(error);

    /// <inheritdoc/>
    TPkt IValueTaskSource<TPkt>.GetResult(short token) => _core.GetResult(token);

    /// <inheritdoc/>
    ValueTaskSourceStatus IValueTaskSource<TPkt>.GetStatus(short token) => _core.GetStatus(token);

    /// <inheritdoc/>
    void IValueTaskSource<TPkt>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);
}
#pragma warning restore CA1001
