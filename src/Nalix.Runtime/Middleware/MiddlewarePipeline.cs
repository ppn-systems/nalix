// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Routing;

namespace Nalix.Runtime.Middleware;

/// <summary>
/// Represents a thread-safe middleware pipeline responsible for processing packets
/// through inbound and outbound stages with configurable error handling.
/// </summary>
/// <typeparam name="TPacket">The packet type being processed by the pipeline.</typeparam>
internal sealed class MiddlewarePipeline<TPacket> where TPacket : IPacket
{
    #region Fields

    private readonly Lock _lock = new();
    private readonly List<MiddlewareEntry> _inbound = [];
    private readonly List<MiddlewareEntry> _outbound = [];
    private readonly List<MiddlewareEntry> _outboundAlways = [];
    private readonly Dictionary<IPacketMiddleware<TPacket>, int> _registeredMiddlewares = [];

    private bool _isSorted;
    private bool _continueOnError;

    private Action<Exception, Type>? _errorHandler;
    private PipelineSnapshot _snapshot = PipelineSnapshot.Empty;

    private static readonly ConcurrentDictionary<Type, MiddlewareMetadata> s_metadataCache = new();
    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    // Instance-level local pool for pipeline contexts to bypass global pool management.
    // 32 slots is sufficient for high concurrency within a single packet pipeline.
    private readonly PooledPipelineContext[] _localPool = new PooledPipelineContext[32];
    private long _localPoolMask;

    private long _activeExecutions;
    private long _totalExecutions;
    private long _totalExecutionTicks;
    private long _maxExecutionTicks;
    private long _totalErrors;

    #endregion Fields

    #region APIs

    /// <summary>
    /// Gets a value indicating whether the pipeline has no middleware in any stage.
    /// </summary>
    public bool IsEmpty => Volatile.Read(ref _snapshot).IsEmpty;

    /// <summary>
    /// Gets the metrics for each individual middleware instance.
    /// </summary>
    public ReadOnlySpan<PerMiddlewareMetrics> MiddlewareMetrics => Volatile.Read(ref _snapshot).Metrics;

    /// <summary>
    /// Gets the aggregated metrics for the pipeline.
    /// </summary>
    public PipelineMetrics Metrics => new(Interlocked.Read(ref _activeExecutions), Interlocked.Read(ref _totalExecutions), Interlocked.Read(ref _totalExecutionTicks), Interlocked.Read(ref _maxExecutionTicks), Interlocked.Read(ref _totalErrors));

    /// <summary>
    /// Clears all registered middleware.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _inbound.Clear();
            _outbound.Clear();
            _outboundAlways.Clear();
            _registeredMiddlewares.Clear();
            _isSorted = true;
            Volatile.Write(ref _snapshot, PipelineSnapshot.Empty);
        }
    }

    /// <summary>
    /// Registers middleware into the pipeline.
    /// </summary>
    /// <param name="middleware">Middleware instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="middleware"/> is null.</exception>
    /// <exception cref="InternalErrorException">Thrown when middleware is already registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for unsupported stage metadata.</exception>
    public void Use(IPacketMiddleware<TPacket> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        lock (_lock)
        {
            if (!_registeredMiddlewares.TryAdd(middleware, _registeredMiddlewares.Count))
            {
                throw new InternalErrorException(
                    $"Middleware '{middleware.GetType().FullName}' already registered");
            }

            int metricIndex = _registeredMiddlewares[middleware];
            MiddlewareMetadata metadata = GetMiddlewareMetadata(middleware.GetType());
            MiddlewareEntry entry = new(middleware, metadata.Order, metricIndex);

            switch (metadata.Stage)
            {
                case MiddlewareStage.Inbound:
                    _inbound.Add(entry);
                    break;
                case MiddlewareStage.Outbound:
                    (metadata.AlwaysExecute ? _outboundAlways : _outbound).Add(entry);
                    break;
                case MiddlewareStage.Both:
                    _inbound.Add(entry);
                    (metadata.AlwaysExecute ? _outboundAlways : _outbound).Add(entry);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(middleware));
            }

            _isSorted = false;
            this.REBUILD_SNAPSHOT_UNSAFE();
        }
    }

    /// <summary>
    /// Configures error handling behavior for middleware execution.
    /// </summary>
    /// <param name="continueOnError">Whether to continue the chain after middleware exceptions.</param>
    /// <param name="errorHandler">Optional callback invoked for middleware exceptions.</param>
    public void ConfigureErrorHandling(bool continueOnError, Action<Exception, Type>? errorHandler = null)
    {
        lock (_lock)
        {
            _continueOnError = continueOnError;
            _errorHandler = errorHandler;
            this.REBUILD_SNAPSHOT_UNSAFE();
        }
    }

    /// <summary>
    /// Executes the middleware pipeline for a packet context with a typed terminal handler.
    /// No per-packet delegate or closure is allocated; the terminal handler is invoked directly.
    /// </summary>
    /// <param name="context">Packet execution context.</param>
    /// <param name="dispatch">Dispatch options that own the terminal handler.</param>
    /// <param name="descriptor">Resolved packet handler descriptor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A value task representing pipeline completion.</returns>
    public ValueTask ExecuteAsync(PacketContext<TPacket> context, PacketDispatchOptions<TPacket> dispatch, PacketHandler<TPacket> descriptor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dispatch);

        _ = Interlocked.Increment(ref _activeExecutions);
        long startTicks = Stopwatch.GetTimestamp();

        PipelineSnapshot snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.IsEmpty)
        {
            ValueTask handlerPending;
            try
            {
                handlerPending = dispatch.ExecuteTerminalHandler(descriptor, context, ct);
            }
            catch
            {
                this.RECORD_EXECUTION(startTicks);
                throw;
            }

            if (handlerPending.IsCompletedSuccessfully)
            {
#pragma warning disable CA1849 // Completed-success fast path; GetResult observes synchronous exceptions without blocking or allocating an async state machine.
                handlerPending.GetAwaiter().GetResult();
#pragma warning restore CA1849
                this.RECORD_EXECUTION(startTicks);
                return ValueTask.CompletedTask;
            }
            return AwaitPendingEmptyAsync(this, handlerPending, startTicks);
        }

        PooledPipelineContext? runner = this.ACQUIRE_RUNNER();
        runner ??= s_pool.Get<PooledPipelineContext>();

        // Initialize for full pipeline execution.
        // Terminal handler info is stored as typed fields — no delegate allocation.
        runner.Initialize(this, snapshot, context, dispatch, descriptor, ct);

        ValueTask pending = runner.RunAsync();
        if (pending.IsCompletedSuccessfully)
        {
            try
            {
#pragma warning disable CA1849 // Completed-success fast path; GetResult observes synchronous exceptions without blocking or allocating an async state machine.
                pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
            }
            finally
            {
                this.RETURN_RUNNER_SYNC(runner);
            }

            this.RECORD_EXECUTION(startTicks);
            return ValueTask.CompletedTask;
        }

        return AwaitPendingAsync(this, pending, runner, startTicks);

        static async ValueTask AwaitPendingAsync(MiddlewarePipeline<TPacket> owner, ValueTask operation, PooledPipelineContext pooledRunner, long startTicks)
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            finally
            {
                owner.RETURN_RUNNER_SYNC(pooledRunner);
                owner.RECORD_EXECUTION(startTicks);
            }
        }

        static async ValueTask AwaitPendingEmptyAsync(MiddlewarePipeline<TPacket> owner, ValueTask operation, long startTicks)
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            finally
            {
                owner.RECORD_EXECUTION(startTicks);
            }
        }
    }

    /// <inheritdoc/>
    public void RecordError() => Interlocked.Increment(ref _totalErrors);

    #endregion APIs

    #region Private Methods

    private static MiddlewareMetadata GetMiddlewareMetadata(Type middlewareType)
    {
        ArgumentNullException.ThrowIfNull(middlewareType);

        return s_metadataCache.GetOrAdd(middlewareType, static type =>
        {
            int order = 0;
            MiddlewareStage stage = MiddlewareStage.Inbound;
            bool alwaysExecute = false;

            if (Attribute.GetCustomAttribute(type, typeof(MiddlewareOrderAttribute))
                is MiddlewareOrderAttribute orderAttr)
            {
                order = orderAttr.Order;
            }

            if (Attribute.GetCustomAttribute(type, typeof(MiddlewareStageAttribute))
                is MiddlewareStageAttribute stageAttr)
            {
                stage = stageAttr.Stage;
                alwaysExecute = stageAttr.AlwaysExecute;
            }

            return new MiddlewareMetadata(order, stage, alwaysExecute);
        });
    }

    private void RECORD_EXECUTION(long startTicks)
    {
        long elapsed = Stopwatch.GetTimestamp() - startTicks;
        _ = Interlocked.Add(ref _totalExecutionTicks, elapsed);
        _ = Interlocked.Increment(ref _totalExecutions);
        _ = Interlocked.Decrement(ref _activeExecutions);
        UPDATE_MAX_TICKS(ref _maxExecutionTicks, elapsed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UPDATE_MAX_TICKS(ref long maxField, long elapsed)
    {
        long max = Volatile.Read(ref maxField);
        while (elapsed > max)
        {
            long prev = Interlocked.CompareExchange(ref maxField, elapsed, max);
            if (prev == max)
            {
                break;
            }

            max = prev;
        }
    }

    private static CancellationTokenSource? CREATE_EXECUTION_TOKEN(CancellationToken inboundToken, CancellationToken rootToken, out CancellationToken effectiveToken)
    {
        if (inboundToken.CanBeCanceled)
        {
            if (!rootToken.CanBeCanceled || inboundToken.Equals(rootToken))
            {
                effectiveToken = inboundToken;
                return null;
            }

            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(inboundToken, rootToken);
            effectiveToken = linked.Token;
            return linked;
        }

        effectiveToken = rootToken;
        return null;
    }

    private PooledPipelineContext? ACQUIRE_RUNNER()
    {
        for (int i = 0; i < 32; i++)
        {
            long bit = 1L << i;
            if ((Interlocked.Read(ref _localPoolMask) & bit) == 0)
            {
                if ((Interlocked.Or(ref _localPoolMask, bit) & bit) == 0)
                {
                    ref PooledPipelineContext r = ref _localPool[i];
                    r ??= new PooledPipelineContext();
                    return r;
                }
            }
        }
        return null;
    }

    private void RETURN_RUNNER_SYNC(PooledPipelineContext runner)
    {
        for (int i = 0; i < 32; i++)
        {
            if (ReferenceEquals(_localPool[i], runner))
            {
                long bit = 1L << i;
                _localPool[i].ResetForPool();
                _ = Interlocked.And(ref _localPoolMask, ~bit);
                return;
            }
        }

        // If it wasn't from our local pool, return to global.
        s_pool.Return<PooledPipelineContext>(runner);
    }

    private void ENSURE_SORTED_UNSAFE()
    {
        if (_isSorted)
        {
            return;
        }

        _inbound.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        _outbound.Sort(static (a, b) => b.Order.CompareTo(a.Order));
        _outboundAlways.Sort(static (a, b) => b.Order.CompareTo(a.Order));
        _isSorted = true;
    }

    private void REBUILD_SNAPSHOT_UNSAFE()
    {
        this.ENSURE_SORTED_UNSAFE();

        MiddlewareEntry[] inbound = new MiddlewareEntry[_inbound.Count];
        for (int i = 0; i < inbound.Length; i++)
        {
            inbound[i] = _inbound[i];
        }

        MiddlewareEntry[] outbound = new MiddlewareEntry[_outbound.Count];
        for (int i = 0; i < outbound.Length; i++)
        {
            outbound[i] = _outbound[i];
        }

        MiddlewareEntry[] outboundAlways = new MiddlewareEntry[_outboundAlways.Count];
        for (int i = 0; i < outboundAlways.Length; i++)
        {
            outboundAlways[i] = _outboundAlways[i];
        }

        PerMiddlewareMetrics[] metrics = new PerMiddlewareMetrics[_registeredMiddlewares.Count];
        foreach (KeyValuePair<IPacketMiddleware<TPacket>, int> kvp in _registeredMiddlewares)
        {
            metrics[kvp.Value] = new PerMiddlewareMetrics { _middlewareType = kvp.Key.GetType() };
        }

        PipelineSnapshot snapshot = new(inbound, outbound, outboundAlways, _continueOnError, _errorHandler, metrics);

        Volatile.Write(ref _snapshot, snapshot);
    }

    #endregion Private Methods

    #region Nested Types

    private readonly record struct MiddlewareEntry(IPacketMiddleware<TPacket> Middleware, int Order, int MetricIndex);

    private readonly record struct MiddlewareMetadata(int Order, MiddlewareStage Stage, bool AlwaysExecute);

    private sealed class PipelineSnapshot
    {
        #region Static Fields

        public static readonly PipelineSnapshot Empty = new([], [], [], continueOnError: false, errorHandler: null, []);

        #endregion Static Fields

        #region Constructors

        public PipelineSnapshot(MiddlewareEntry[] inbound, MiddlewareEntry[] outbound, MiddlewareEntry[] outboundAlways, bool continueOnError, Action<Exception, Type>? errorHandler, PerMiddlewareMetrics[] metrics)
        {
            this.Inbound = inbound;
            this.Outbound = outbound;
            this.OutboundAlways = outboundAlways;
            this.ContinueOnError = continueOnError;
            this.ErrorHandler = errorHandler;
            this.Metrics = metrics;
        }

        #endregion Constructors

        #region Properties

        public bool ContinueOnError { get; }

        public MiddlewareEntry[] Inbound { get; }

        public MiddlewareEntry[] Outbound { get; }

        public MiddlewareEntry[] OutboundAlways { get; }

        public Action<Exception, Type>? ErrorHandler { get; }

        public PerMiddlewareMetrics[] Metrics { get; }

        public bool IsEmpty => this.Inbound.Length == 0 && this.Outbound.Length == 0 && this.OutboundAlways.Length == 0;

        #endregion Properties
    }

    private sealed class PooledPipelineContext : IPoolable
    {
        #region Fields

        private bool _continueOnError;
        private CancellationToken _startToken;
        private CancellationToken _rootCt;
        private PacketContext<TPacket>? _context;
        private MiddlewareEntry[] _middlewares = [];
        private Action<Exception, Type>? _errorHandler;

        private Func<CancellationToken, ValueTask>? _final;
        private Func<CancellationToken, ValueTask>[] _steps = [];

        // Full pipeline state — typed terminal handler (no delegate allocation).
        private MiddlewarePipeline<TPacket>? _owner;
        private PipelineSnapshot? _snapshot;
        private PipelineStage _currentStage;
        private PacketDispatchOptions<TPacket>? _dispatch;
        private PacketHandler<TPacket> _handler;

        #endregion Fields

        #region APIs

        public void Initialize(
            MiddlewarePipeline<TPacket> owner,
            PipelineSnapshot snapshot,
            PacketContext<TPacket> context,
            PacketDispatchOptions<TPacket> dispatch,
            PacketHandler<TPacket> handler,
            CancellationToken ct)
        {
            _owner = owner;
            _snapshot = snapshot;
#pragma warning disable NALIX076 // MiddlewarePipeline intentionally holds context during execution; cleared in ResetForPool.
            _context = context;
#pragma warning restore NALIX076
            _dispatch = dispatch;
            _handler = handler;
            _rootCt = ct;
            _startToken = ct;
            _continueOnError = snapshot.ContinueOnError;
            _errorHandler = snapshot.ErrorHandler;

            this.SET_STAGE(PipelineStage.Inbound);
        }

        public void ResetForPool()
        {
            _middlewares = [];
            _context = null;
            _final = null;
            _startToken = default;
            _rootCt = default;
            _continueOnError = false;
            _errorHandler = null;
            _owner = null;
            _snapshot = null;
            _dispatch = null;
            _handler = default;
            _currentStage = PipelineStage.None;
        }

        public ValueTask RunAsync() => _steps[0](_startToken);

        #endregion APIs

        #region Private Methods

        private void SET_STAGE(PipelineStage stage)
        {
            _currentStage = stage;
            _middlewares = stage switch
            {
                PipelineStage.Inbound => _snapshot!.Inbound,
                PipelineStage.OutboundAlways => _snapshot!.OutboundAlways,
                PipelineStage.Outbound => _snapshot!.Outbound,
                PipelineStage.None => throw new NotImplementedException(),
                PipelineStage.Mid => throw new NotImplementedException(),
                PipelineStage.Finished => throw new NotImplementedException(),
                _ => []
            };

            this.ENSURE_STEPS(_middlewares.Length + 1);
        }

        private void ENSURE_STEPS(int requiredLength)
        {
            if (_steps.Length >= requiredLength)
            {
                return;
            }

            int currentLength = _steps.Length;
            Array.Resize(ref _steps, requiredLength);

            for (int i = currentLength; i < requiredLength; i++)
            {
                int index = i;
                _steps[i] = token => this.INVOKE_ASYNC(index, token);
            }
        }

        private ValueTask INVOKE_ASYNC(int index, CancellationToken token)
        {
            if ((uint)index >= (uint)_middlewares.Length)
            {
                // Last step in current stage
                if (_currentStage == PipelineStage.Mid)
                {
                    Func<CancellationToken, ValueTask>? final = _final;
                    return final is null ? ValueTask.CompletedTask : final(token);
                }

                return this.TRANSITION_ASYNC(token);
            }

            PacketContext<TPacket>? context = _context;
            if (context is null)
            {
                return ValueTask.FromException(
                    new InternalErrorException("Middleware pipeline runner is not initialized."));
            }

            MiddlewareEntry entry = _middlewares[index];
            Func<CancellationToken, ValueTask> next = _steps[index + 1];

            long startTicks = Stopwatch.GetTimestamp();

            try
            {
                ValueTask pending = entry.Middleware.InvokeAsync(context, next);
                if (pending.IsCompletedSuccessfully)
                {
                    long elapsed = Stopwatch.GetTimestamp() - startTicks;
                    if (_snapshot is not null)
                    {
                        _ = Interlocked.Add(ref _snapshot.Metrics[entry.MetricIndex]._totalExecutionTicks, elapsed);
                        _ = Interlocked.Increment(ref _snapshot.Metrics[entry.MetricIndex]._totalExecutions);
                        UPDATE_MAX_TICKS(ref _snapshot.Metrics[entry.MetricIndex]._maxExecutionTicks, elapsed);
                    }
                    return pending;
                }

                if (!_continueOnError)
                {
                    return AWAIT_AND_RECORD_ASYNC(this, pending, entry, startTicks);
                }

                return AWAIT_WITH_CONTINUE_AND_RECORD_ASYNC(this, pending, entry, next, token, startTicks);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (_snapshot is not null)
                {
                    _ = Interlocked.Increment(ref _snapshot.Metrics[entry.MetricIndex]._totalErrors);
                }
                _owner?.RecordError();

                if (!_continueOnError)
                {
                    return ValueTask.FromException(ex);
                }

                _errorHandler?.Invoke(ex, entry.Middleware.GetType());
                return next(token);
            }
        }

        private ValueTask TRANSITION_ASYNC(CancellationToken token)
        {
            switch (_currentStage)
            {
                case PipelineStage.Inbound:
                    {
                        ValueTask pending = this.EXECUTE_HANDLER_FULL_ASYNC(token);
                        if (pending.IsCompletedSuccessfully)
                        {
#pragma warning disable CA1849
                            pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
                            return default;
                        }
                        return AWAIT_TRANSITION_INBOUND_ASYNC(pending);
                    }
                case PipelineStage.OutboundAlways:
                    if (!_context!.SkipOutbound && !token.IsCancellationRequested)
                    {
                        this.SET_STAGE(PipelineStage.Outbound);
                        ValueTask pending = this.RunAsync();
                        if (pending.IsCompletedSuccessfully)
                        {
#pragma warning disable CA1849
                            pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
                            return default;
                        }
                        return AWAIT_TRANSITION_OUTBOUND_ASYNC(pending);
                    }
                    else
                    {
                        _currentStage = PipelineStage.Finished;
                        return default;
                    }

                case PipelineStage.Outbound:
                    _currentStage = PipelineStage.Finished;
                    return default;
                case PipelineStage.None:
                case PipelineStage.Mid:
                case PipelineStage.Finished:
                default:
                    return default;
            }

            static async ValueTask AWAIT_TRANSITION_INBOUND_ASYNC(ValueTask pending) => await pending.ConfigureAwait(false);

            static async ValueTask AWAIT_TRANSITION_OUTBOUND_ASYNC(ValueTask pending) => await pending.ConfigureAwait(false);
        }

        private ValueTask EXECUTE_HANDLER_FULL_ASYNC(CancellationToken inboundCt)
        {
            CancellationTokenSource? linkedCts = CREATE_EXECUTION_TOKEN(inboundCt, _rootCt, out CancellationToken handlerCt);

            // Direct terminal handler invocation — no delegate allocation.
            // A synchronous throw is converted to a faulted task so OutboundAlways still runs.
            ValueTask handlerPending;
            try
            {
                handlerPending = _dispatch!.ExecuteTerminalHandler(_handler, _context!, handlerCt);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                handlerPending = ValueTask.FromException(ex);
            }

            if (handlerPending.IsCompletedSuccessfully)
            {
#pragma warning disable CA1849 // Completed-success fast path.
                handlerPending.GetAwaiter().GetResult();
#pragma warning restore CA1849
                linkedCts?.Dispose();

                this.SET_STAGE(PipelineStage.OutboundAlways);
                ValueTask outboundPending = this.RunAsync();
                if (outboundPending.IsCompletedSuccessfully)
                {
#pragma warning disable CA1849
                    outboundPending.GetAwaiter().GetResult();
#pragma warning restore CA1849
                    return default;
                }
                return AWAIT_OUTBOUND_ASYNC(outboundPending);
            }

            return AWAIT_HANDLER_AND_OUTBOUND_ASYNC(this, handlerPending, handlerCt, linkedCts);

            static async ValueTask AWAIT_OUTBOUND_ASYNC(ValueTask outbound) => await outbound.ConfigureAwait(false);

            static async ValueTask AWAIT_HANDLER_AND_OUTBOUND_ASYNC(
                PooledPipelineContext runner, ValueTask handler, CancellationToken handlerCt, CancellationTokenSource? linkedCts)
            {
                OperationCanceledException? cancelled = null;
                ExceptionDispatchInfo? failure = null;
                try
                {
                    try
                    {
                        await handler.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (handlerCt.IsCancellationRequested)
                    {
                        // Still run OutboundAlways below, but propagate the cancellation once it
                        // completes so callers (e.g. TimeoutMiddleware) can observe and react to it.
                        cancelled = ex;
                    }
                    catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                    {
                        // OutboundAlways must run for handler failures too; rethrow after it completes.
                        failure = ExceptionDispatchInfo.Capture(ex);
                    }

                    runner.SET_STAGE(PipelineStage.OutboundAlways);
                    await runner.RunAsync().ConfigureAwait(false);

                    failure?.Throw();

                    if (cancelled is not null)
                    {
                        throw cancelled;
                    }
                }
                finally
                {
                    linkedCts?.Dispose();
                }
            }
        }

        private static async ValueTask AWAIT_WITH_CONTINUE_AND_RECORD_ASYNC(
            PooledPipelineContext runner,
            ValueTask pending,
            MiddlewareEntry entry,
            Func<CancellationToken, ValueTask> next,
            CancellationToken token,
            long startTicks)
        {
            try
            {
                await pending.ConfigureAwait(false);

                long elapsed = Stopwatch.GetTimestamp() - startTicks;
                if (runner._snapshot is not null)
                {
                    _ = Interlocked.Add(ref runner._snapshot.Metrics[entry.MetricIndex]._totalExecutionTicks, elapsed);
                    _ = Interlocked.Increment(ref runner._snapshot.Metrics[entry.MetricIndex]._totalExecutions);
                    UPDATE_MAX_TICKS(ref runner._snapshot.Metrics[entry.MetricIndex]._maxExecutionTicks, elapsed);
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (runner._snapshot is not null)
                {
                    _ = Interlocked.Increment(ref runner._snapshot.Metrics[entry.MetricIndex]._totalErrors);
                }
                runner._owner?.RecordError();

                runner._errorHandler?.Invoke(ex, entry.Middleware.GetType());
                await next(token).ConfigureAwait(false);
            }
        }

        private static async ValueTask AWAIT_AND_RECORD_ASYNC(PooledPipelineContext runner, ValueTask pending, MiddlewareEntry entry, long startTicks)
        {
            await pending.ConfigureAwait(false);
            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            if (runner._snapshot is not null)
            {
                _ = Interlocked.Add(ref runner._snapshot.Metrics[entry.MetricIndex]._totalExecutionTicks, elapsed);
                _ = Interlocked.Increment(ref runner._snapshot.Metrics[entry.MetricIndex]._totalExecutions);
                UPDATE_MAX_TICKS(ref runner._snapshot.Metrics[entry.MetricIndex]._maxExecutionTicks, elapsed);
            }
        }

        #endregion Private Methods

        private enum PipelineStage
        {
            None,
            Inbound,
            Mid,
            OutboundAlways,
            Outbound,
            Finished
        }
    }

    #endregion Nested Types
}
