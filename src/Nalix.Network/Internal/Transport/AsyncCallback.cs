// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Environment.Time;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Options;

#if DEBUG
[assembly: InternalsVisibleTo("Nalix.Network.Tests")]
[assembly: InternalsVisibleTo("Nalix.Network.Benchmarks")]
#endif

namespace Nalix.Network.Internal.Transport;

/// <summary>
/// Specifies the processing lane for a network callback to ensure fairness and prevent starvation.
/// </summary>
internal enum CallbackLane
{
    /// <summary>
    /// Lane for incoming data processing (OnFrameReceived).
    /// </summary>
    Process,
    /// <summary>
    /// Lane for post-send processing (OnFrameSent).
    /// </summary>
    Post
}

/// <summary>
/// Contains metrics about the AsyncCallback dispatcher.
/// </summary>
internal readonly struct AsyncCallbackMetrics
{
    public readonly int PendingProcess;
    public readonly int PendingPost;
    public readonly long Dropped;
    public readonly long Total;
    public readonly long SlowCallbacks;
    public readonly long FairnessCollisions;
    public readonly long FairnessEvictions;

    public AsyncCallbackMetrics(int pendingProcess, int pendingPost, long dropped, long total, long slowCallbacks, long fairnessCollisions, long fairnessEvictions)
    {
        PendingProcess = pendingProcess;
        PendingPost = pendingPost;
        Dropped = dropped;
        Total = total;
        SlowCallbacks = slowCallbacks;
        FairnessCollisions = fairnessCollisions;
        FairnessEvictions = fairnessEvictions;
    }
}

/// <summary>
/// Fire-and-forget dispatcher that offloads callbacks to the ThreadPool.
///
/// <para><b>Layer 2 — Two-priority dispatch:</b><br/>
/// Callbacks are split into two lanes:
/// <list type="bullet">
///   <item><b>High priority</b> — close/disconnect events. Always dispatched immediately,
///         not subject to backpressure limits.</item>
///   <item><b>Normal priority</b> — process/post-process packet events. Subject to the
///         global caps. Separated into Process and Post lanes.</item>
/// </list>
/// </para>
/// </summary>
[DebuggerNonUserCode]
[ExcludeFromCodeCoverage]
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class AsyncCallback
{
    #region Options

    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;
    private static readonly NetworkCallbackOptions s_netOpts = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();

    #endregion Options

    #region Fields

    /// <summary>
    /// ── Global counters ────────────────────────────────────────────────────────
    /// </summary>
    private static int s_pendingProcess;
    private static int s_pendingPost;
    private static long s_totalInvoked;
    private static long s_droppedCallbacks;

    // Metrics
    private static long s_slowCallbacks;
    private static long s_fairnessCollisions;
    private static long s_fairnessEvictions;

    private static long s_globalBackpressureTicks;
    private static long s_globalBackpressureSuppressed;
    private static long s_perIpBackpressureTicks;
    private static long s_perIpBackpressureSuppressed;
    private static long s_highBackpressureTicks;
    private static long s_highBackpressureSuppressed;
    private static long s_queueExceptionTicks;
    private static long s_queueExceptionSuppressed;
    private static long s_callbackErrorTicks;
    private static long s_callbackErrorSuppressed;
    private static long s_slowCallbackTicks;
    private static long s_slowCallbackSuppressed;
    private static long s_queueFailedTicks;
    private static long s_queueFailedSuppressed;

    static AsyncCallback()
    {
        s_perIpPostMap = new long[s_netOpts.FairnessMapSize];
        s_perIpProcessMap = new long[s_netOpts.FairnessMapSize];
    }

    /// <summary>
    /// ── Per-IP pending counter ─────────────────────────────────────────────────
    /// </summary>
    private static readonly long[] s_perIpPostMap;
    private static readonly long[] s_perIpProcessMap;

    /// <summary>
    /// ── Static delegates — no closures, no per-invocation allocations ──────────
    /// </summary>
    private static readonly Action<object> s_invokeProcess = static stateObj =>
    {
        if (stateObj is not PooledConnectEventContext w)
        {
            return;
        }

        _ = Interlocked.Decrement(ref s_pendingProcess);

        INetworkEndpoint? endpoint = GET_ENDPOINT_SAFE(w.Args);
        RELEASE_ENDPOINT_SLOT(endpoint, CallbackLane.Process);
        EXECUTE_AND_RETURN(w);
    };

    private static readonly Action<object> s_invokePost = static stateObj =>
    {
        if (stateObj is not PooledConnectEventContext w)
        {
            return;
        }

        _ = Interlocked.Decrement(ref s_pendingPost);

        INetworkEndpoint? endpoint = GET_ENDPOINT_SAFE(w.Args);
        RELEASE_ENDPOINT_SLOT(endpoint, CallbackLane.Post);
        EXECUTE_AND_RETURN(w);
    };

    private static readonly Action<object> s_invokeHigh = static stateObj =>
    {
        if (stateObj is not PooledConnectEventContext w)
        {
            return;
        }
        // High-priority callbacks are not counted against any limit — just run.
        EXECUTE_AND_RETURN(w);
    };

    #endregion Fields

    #region Public Methods

    /// <summary>
    /// Schedules a <b>normal-priority</b> (process / post-process) callback on the ThreadPool.
    /// Subject to global and per-IP backpressure limits.
    /// </summary>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Invoke(
        EventHandler<IConnectionEventArgs>? callback,
        object? sender,
        IConnectionEventArgs args,
        CallbackLane lane,
        bool releasePendingPacketOnCompletion = false)
    {
        if (callback is null)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.AsyncCallback:Invoke", "callback-null skipping"));
            }
            return false;
        }

        if (!TRY_RESERVE_GLOBAL_SLOT(lane, out int globalPending))
        {
            _ = Interlocked.Increment(ref s_droppedCallbacks);
            LOG_THROTTLED_ERROR_SAFE(ref s_globalBackpressureTicks, ref s_globalBackpressureSuppressed, "async.global_backpressure");
            return false;
        }

        INetworkEndpoint? endpoint = GET_ENDPOINT_SAFE(args);
        if (!TRY_RESERVE_ENDPOINT_SLOT(endpoint, lane, out int ipPending))
        {
            RELEASE_GLOBAL_SLOT(lane);
            _ = Interlocked.Increment(ref s_droppedCallbacks);
            _ = lane == CallbackLane.Process ? s_netOpts.MaxPendingPerIp : s_netOpts.MaxPendingPostPerIp;
            LOG_THROTTLED_WARN_SAFE(ref s_perIpBackpressureTicks, ref s_perIpBackpressureSuppressed, "async.per_ip_backpressure");
            return false;
        }

        int warnThreshold = s_netOpts.CallbackWarningThreshold;
        if (warnThreshold > 0 && globalPending >= warnThreshold && globalPending % 1_000 == 0)
        {
            _ = lane == CallbackLane.Process ? s_netOpts.MaxPendingNormalCallbacks : s_netOpts.MaxPendingPostCallbacks;
            LOG_THROTTLED_WARN_SAFE(ref s_highBackpressureTicks, ref s_highBackpressureSuppressed, "async.high_backpressure");
        }

        _ = Interlocked.Increment(ref s_totalInvoked);

        Action<object> invoker = lane == CallbackLane.Process ? s_invokeProcess : s_invokePost;
        return QUEUE(invoker, callback, sender, args, isHigh: false, releasePendingPacketOnCompletion, lane);
    }

    /// <summary>
    /// Schedules a <b>high-priority</b> (close / disconnect) callback on the ThreadPool.
    /// </summary>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool InvokeHighPriority(
        EventHandler<IConnectionEventArgs>? callback,
        object? sender,
        IConnectionEventArgs args)
    {
        if (callback is null)
        {
            return false;
        }

        _ = Interlocked.Increment(ref s_totalInvoked);

        return QUEUE(s_invokeHigh, callback, sender, args, isHigh: true, releasePendingPacketOnCompletion: false, CallbackLane.Process);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the frame handler is a single <see cref="IFrameProcessor"/>
    /// that declared <see cref="IFrameProcessor.SupportsInlineProcessing"/>, so the receive
    /// completion may run it directly instead of queuing a thread-pool work item.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanRunInline([NotNullWhen(true)] EventHandler<IConnectionEventArgs>? callback)
        => callback is not null
           && callback.HasSingleTarget
           && callback.Target is IFrameProcessor { SupportsInlineProcessing: true };

    /// <summary>
    /// Runs a process-lane callback synchronously on the calling (receive) thread.
    /// </summary>
    /// <remarks>
    /// Used only for frame processors that declared themselves non-blocking (see
    /// <see cref="CanRunInline"/>). Queuing such a callback adds a thread-pool hop per frame and
    /// buys no isolation: the processor only transforms the frame and hands it to the dispatch
    /// queue, which already decouples handler execution from the transport. Running it inline
    /// also keeps per-connection frame order. Exceptions are contained exactly like the queued path.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InvokeInline(EventHandler<IConnectionEventArgs> callback, object? sender, IConnectionEventArgs args)
    {
        _ = Interlocked.Increment(ref s_totalInvoked);

        try
        {
            callback(sender, args);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            LOG_THROTTLED_ERROR_SAFE(ref s_callbackErrorTicks, ref s_callbackErrorSuppressed, "async.callback_error", ex);
        }
    }

    /// <summary>Gets diagnostic statistics about callback processing.</summary>
    public static AsyncCallbackMetrics GetStatistics()
        => new(
            Volatile.Read(ref s_pendingProcess),
            Volatile.Read(ref s_pendingPost),
            Volatile.Read(ref s_droppedCallbacks),
            Volatile.Read(ref s_totalInvoked),
            Volatile.Read(ref s_slowCallbacks),
            Volatile.Read(ref s_fairnessCollisions),
            Volatile.Read(ref s_fairnessEvictions));

    /// <summary>Resets dropped/total counters. Used for testing or periodic reporting.</summary>
    internal static void ResetStatistics()
    {
        _ = Interlocked.Exchange(ref s_droppedCallbacks, 0);
        _ = Interlocked.Exchange(ref s_totalInvoked, 0);
        _ = Interlocked.Exchange(ref s_slowCallbacks, 0);
        _ = Interlocked.Exchange(ref s_fairnessCollisions, 0);
        _ = Interlocked.Exchange(ref s_fairnessEvictions, 0);
    }

    #endregion Public Methods

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool QUEUE(
        Action<object> invoker,
        EventHandler<IConnectionEventArgs> callback,
        object? sender,
        IConnectionEventArgs args,
        bool isHigh,
        bool releasePendingPacketOnCompletion,
        CallbackLane lane)
    {
        PooledConnectEventContext wrapper;
        if (sender is IPooledConnectContextPool owner)
        {
            wrapper = owner.AcquireContext() ?? throw new InvalidOperationException("Failed to acquire context.");
        }
        else
        {
            wrapper = s_pool.Get<PooledConnectEventContext>();
            wrapper.LocalOwner = null;
        }

        wrapper.SetThreadPoolInvoker(invoker);
        wrapper.Initialize(callback, sender, args, releasePendingPacketOnCompletion);

        bool queued = QUEUE_SAFE(wrapper, preferLocal: isHigh);

        if (!queued)
        {
            INetworkEndpoint? endpoint = GET_ENDPOINT_SAFE(args);
            if (!isHigh)
            {
                RELEASE_GLOBAL_SLOT(lane);
                RELEASE_ENDPOINT_SLOT(endpoint, lane);
            }

            _ = Interlocked.Increment(ref s_droppedCallbacks);
            LOG_THROTTLED_ERROR_SAFE(ref s_queueFailedTicks, ref s_queueFailedSuppressed, "async.queue_failed");

            wrapper.Dispose();

            return false;
        }

        return queued;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool QUEUE_SAFE(PooledConnectEventContext wrapper, bool preferLocal)
    {
        try
        {
            // PooledConnectEventContext implements IThreadPoolWorkItem, so the runtime
            // calls Execute() directly without allocating a QueueUserWorkItemCallbackDefaultContext wrapper.
            return ThreadPool.UnsafeQueueUserWorkItem(wrapper, preferLocal);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            LOG_THROTTLED_ERROR_SAFE(ref s_queueExceptionTicks, ref s_queueExceptionSuppressed, "async.queue_exception", ex);
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TRY_RESERVE_GLOBAL_SLOT(CallbackLane lane, out int pendingAfter)
    {
        ref int pending = ref (lane == CallbackLane.Process ? ref s_pendingProcess : ref s_pendingPost);
        int max = lane == CallbackLane.Process ? s_netOpts.MaxPendingNormalCallbacks : s_netOpts.MaxPendingPostCallbacks;

        while (true)
        {
            int observed = Volatile.Read(ref pending);
            if (observed >= max)
            {
                pendingAfter = observed;
                return false;
            }

            pendingAfter = observed + 1;
            if (Interlocked.CompareExchange(ref pending, pendingAfter, observed) == observed)
            {
                return true;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RELEASE_GLOBAL_SLOT(CallbackLane lane)
    {
        if (lane == CallbackLane.Process)
        {
            _ = Interlocked.Decrement(ref s_pendingProcess);
        }
        else
        {
            _ = Interlocked.Decrement(ref s_pendingPost);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TRY_RESERVE_ENDPOINT_SLOT(INetworkEndpoint? endpoint, CallbackLane lane, out int pendingAfter)
    {
        int hash = endpoint?.GetHashCode() ?? 0;
        long[] map = lane == CallbackLane.Process ? s_perIpProcessMap : s_perIpPostMap;
        int max = lane == CallbackLane.Process ? s_netOpts.MaxPendingPerIp : s_netOpts.MaxPendingPostPerIp;

        int startIndex = (hash & 0x7FFFFFFF) % map.Length;

        for (int probe = 0; probe < 12; probe++) // Increased probe depth
        {
            int index = (startIndex + probe) % map.Length;
            if (probe > 0)
            {
                _ = Interlocked.Increment(ref s_fairnessCollisions);
            }

            while (true)
            {
                long current = Volatile.Read(ref map[index]);
                int currentHash = (int)(current >> 32);
                int currentCount = (int)current;

                if (currentCount > 0 && currentHash != hash)
                {
                    break; // Collision, try next slot
                }

                if (currentCount >= max)
                {
                    pendingAfter = currentCount;
                    return false; // Rate limited
                }

                long newValue = ((long)hash << 32) | (uint)(currentCount + 1);
                if (Interlocked.CompareExchange(ref map[index], newValue, current) == current)
                {
                    pendingAfter = currentCount + 1;
                    return true;
                }
            }
        }

        _ = Interlocked.Increment(ref s_fairnessEvictions);
        pendingAfter = max;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RELEASE_ENDPOINT_SLOT(INetworkEndpoint? endpoint, CallbackLane lane)
    {
        int hash = endpoint?.GetHashCode() ?? 0;
        long[] map = lane == CallbackLane.Process ? s_perIpProcessMap : s_perIpPostMap;
        int startIndex = (hash & 0x7FFFFFFF) % map.Length;

        for (int probe = 0; probe < 12; probe++) // Match new probe depth
        {
            int index = (startIndex + probe) % map.Length;
            while (true)
            {
                long current = Volatile.Read(ref map[index]);
                int currentHash = (int)(current >> 32);
                int currentCount = (int)current;

                if (currentCount == 0)
                {
                    break; // Empty slot, continue probing because it might have been freed!
                }

                if (currentHash != hash)
                {
                    break; // Not our slot, continue probing
                }

                long newValue = ((long)hash << 32) | (uint)(currentCount - 1);
                if (Interlocked.CompareExchange(ref map[index], newValue, current) == current)
                {
                    return;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static INetworkEndpoint? GET_ENDPOINT_SAFE(IConnectionEventArgs? args)
    {
        if (args is null)
        {
            return null;
        }

        try { return args.NetworkEndpoint; }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { return null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LOG_THROTTLED_WARN_SAFE(ref long ticks, ref long suppressedCount, string eventName)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
        {
            if (Security.ThrottledEventGate.TryAcquire(ref ticks, ref suppressedCount, DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5, out long suppressed))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.AsyncCallback:Internal", $"message event={eventName} suppressed-count={suppressed}"));
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LOG_THROTTLED_ERROR_SAFE(ref long ticks, ref long suppressedCount, string eventName, Exception? ex = null)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
        {
            if (Security.ThrottledEventGate.TryAcquire(ref ticks, ref suppressedCount, DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5, out long suppressed))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error,
                        new DiagnosticLog("NW.AsyncCallback:Internal", $"message event={eventName} suppressed-count={suppressed}", ex));
                }
                ;
            }
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EXECUTE_AND_RETURN(PooledConnectEventContext w)
    {
        IConnectionEventArgs? args = w.Args;

        long startMonoTicks = Clock.MonoTicksNow();
        try
        {
            w.Callback?.Invoke(w.Sender, args);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            LOG_THROTTLED_ERROR_SAFE(ref s_callbackErrorTicks, ref s_callbackErrorSuppressed, "async.callback_error", ex);
        }
        finally
        {
            double elapsedMs = Clock.MonoTicksToMilliseconds(Clock.MonoTicksNow() - startMonoTicks);
            if (elapsedMs > s_netOpts.MaxCallbackExecutionMs)
            {
                _ = Interlocked.Increment(ref s_slowCallbacks);
                LOG_THROTTLED_WARN_SAFE(ref s_slowCallbackTicks, ref s_slowCallbackSuppressed, "async.slow_callback");
            }

            if (w.ReleasePendingPacketOnCompletion && w.Sender is IPooledConnectContextPool owner)
            {
                owner.ReleasePendingPacket();
            }

            w.Dispose();
        }
    }

    #endregion Private Helpers
}
