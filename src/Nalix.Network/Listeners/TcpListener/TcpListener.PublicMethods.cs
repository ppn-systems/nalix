// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Framework.Injection;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;

namespace Nalix.Network.Listeners.Tcp;

/// <summary>
/// An abstract base class for network listeners.
/// This class manages the process of accepting incoming network connections
/// and handling the associated protocol processing.
/// It owns the listener lifecycle, the accept workers, and the shutdown flow
/// for a concrete TCP listener implementation.
/// </summary>
[DebuggerDisplay("Port={_port}, StateWrapper={StateWrapper}")]
public abstract partial class TcpListenerBase
{
    /// <summary>
    /// Starts listening for incoming connections and processes them using the specified protocol.
    /// The listening process can be cancelled using the provided <see cref="CancellationToken"/>.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> to cancel the listening process.</param>
    /// <exception cref="InternalErrorException"></exception>
    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void Activate(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        if (_config.MaxParallel < 1)
        {
            throw new InternalErrorException("_config.MaxParallel must be at least 1.");
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog("NW.TcpListenerBase:Activate", $"activate-request port={_port}"));
        }

        // Avoid blocking lifecycle transitions behind a concurrent caller.
        if (!_lock.Wait(0, CancellationToken.None))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog("NW.TcpListenerBase:Activate", $"activate-skipped lock-busy port={_port}"));
            }

            return;
        }

        CancellationToken linkedToken = default;

        try
        {
            // Check state while holding the lock so two concurrent Activate calls
            // cannot both observe STOPPED and initialize twice.
            if ((ListenerState)Volatile.Read(ref _state) != ListenerState.STOPPED)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:Activate", $"ignored-activate state={this.State}"));
                }

                return;
            }

            _ = Interlocked.Exchange(ref _stopInitiated, 0);
            _ = Interlocked.Exchange(ref _state, (int)ListenerState.STARTING);

            // Create a linked CTS so cancellation from the caller propagates to
            // every worker, timeout job, and background accept loop.
            // Disposing the previous CTS first avoids leaking registrations when
            // Activate/Deactivate cycles happen repeatedly.
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellationToken = _cts.Token;

            linkedToken = _cts.Token;
            _cancelReg = linkedToken.Register(static s =>
            {
                if (s is TcpListenerBase listener)
                {
                    listener.SCHEDULE_STOP();
                }
            }, this);

            bool needInit;
            try
            {
                Socket? listener = _listener;
                needInit = listener is null || !listener.IsBound || listener.SafeHandle.IsInvalid;
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                needInit = true;
            }

            if (needInit)
            {
                this.Initialize();
            }

            _ = Interlocked.Exchange(ref _state, (int)ListenerState.RUNNING);

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information,
                    new DiagnosticLog("NW.TcpListenerBase:Activate", $"start protocol={_protocol} port={_port}"));
            }

            if (_config.EnableTimeout)
            {
                _timing.Activate(linkedToken);
            }

            this.START_PROCESS_CHANNEL(linkedToken);

            // Spawn N accept-worker async tasks, where N = MaxParallel.
            // Multiple workers let the listener accept several connections in
            // parallel instead of serializing every accept behind one loop.
            IWorkerHandle[] acceptWorkers = new IWorkerHandle[_config.MaxParallel];
            for (int i = 0; i < _config.MaxParallel; i++)
            {
                acceptWorkers[i] = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleWorker(
                    name: $"{TaskNaming.Tags.Tcp}.{TaskNaming.Tags.Accept}.{i}",
                    group: $"{TaskNaming.Tags.Net}/{TaskNaming.Tags.Tcp}/{_port}",
                    work: async (ctx, ct) => await this.AcceptConnectionsAsync(ctx, ct).ConfigureAwait(false),
                    options: new WorkerOptions
                    {
                        Tag = TaskNaming.Tags.Net,
                        IdType = SnowflakeType.System,
                        CancellationToken = linkedToken,
                        RetainFor = TimeSpan.FromSeconds(30),
                    }
                );
            }
            _acceptWorkers = acceptWorkers;

            // Wait until at least one accept worker and the process-channel worker are actually running before returning.
            // Without this, Activate() returns before any accept/dispatch loop has started, creating
            // a race between client connections and server readiness.
            {
                SpinWait spin = new();
                long deadlineMs = System.Environment.TickCount64 + 5000;
                while (System.Environment.TickCount64 < deadlineMs)
                {
                    bool anyRunning = false;
                    for (int w = 0; w < acceptWorkers.Length; w++)
                    {
                        if (acceptWorkers[w].IsRunning)
                        {
                            anyRunning = true;
                            break;
                        }
                    }
                    if (anyRunning && (_processWorker?.IsRunning ?? true))
                    {
                        break;
                    }

                    spin.SpinOnce();
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.TcpListenerBase:Activate", $"cancel port={_port}"));
            }

            _ = Interlocked.Exchange(ref _state, (int)ListenerState.STOPPED);
        }
        catch (SocketException ex)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted,
                    new DiagnosticLog("NW.TcpListenerBase:Activate", $"start-failed port={_port}", ex));
            }

            _ = Interlocked.Exchange(ref _state, (int)ListenerState.STOPPED);
            throw;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Critical))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Critical,
                    new DiagnosticLog("NW.TcpListenerBase:Activate", $"critical-error port={_port}", ex));
            }

            _ = Interlocked.Exchange(ref _state, (int)ListenerState.STOPPED);
            throw;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <summary>
    /// Stops the listener from accepting further connections.
    /// </summary>
    /// <param name="cancellationToken">A token that may be used by derived implementations during shutdown.</param>
    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void Deactivate(CancellationToken cancellationToken = default)
    {
        // If the listener is already disposed and fully stopped, there is nothing
        // left to deactivate.
        if (Volatile.Read(ref _isDisposed) != 0 && this.State == ListenerState.STOPPED)
        {
            return;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"deactivate-request port={_port}"));
        }

        // Try RUNNING -> STOPPING first; if that fails, allow STARTING -> STOPPING
        // so shutdown works even while activation is still in progress.
        int prev = Interlocked.CompareExchange(ref _state,
            (int)ListenerState.STOPPING, (int)ListenerState.RUNNING);

        if (prev != (int)ListenerState.RUNNING)
        {
            prev = Interlocked.CompareExchange(ref _state,
                (int)ListenerState.STOPPING, (int)ListenerState.STARTING);

            if (prev != (int)ListenerState.STARTING)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"ignored-deactivate state={this.State}"));
                }

                return;
            }
        }

        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        try
        {
            try
            {
                _cancelReg.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"cancel-reg-dispose-ignored port={_port} exception-type={ex.GetType().Name}", ex));
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"cancel-reg-dispose-failed port={_port}", ex));
                }
            }

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"cts-cancel-ignored port={_port} exception-type={ex.GetType().Name}", ex));
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"cts-cancel-failed port={_port}", ex));
                }
            }

            try
            {
                _listener?.Close();
            }
            catch (ObjectDisposedException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"listener-close-ignored port={_port} exception-type={ex.GetType().Name}", ex));
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"listener-close-failed port={_port}", ex));
                }
            }

            _listener = null;

            this.STOP_PROCESS_CHANNEL();

            IWorkerHandle[]? acceptWorkers = Interlocked.Exchange(ref _acceptWorkers, null);
            if (acceptWorkers != null)
            {
                foreach (IWorkerHandle? worker in acceptWorkers)
                {
                    worker?.Dispose();
                }
            }

            if (_config.EnableTimeout)
            {
                _timing.Deactivate(CancellationToken.None);
            }

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information,
                    new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"stop protocol={_protocol} port={_port}"));
            }
        }
        finally
        {
            try
            {
                cts?.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"cts-dispose-ignored port={_port} exception-type={ex.GetType().Name}", ex));
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:Deactivate", $"cts-dispose-failed port={_port}", ex));
                }
            }

            _cts = null;
            _ = Interlocked.Exchange(ref _state, (int)ListenerState.STOPPED);
        }
    }

    #region IReportable Implementation

    /// <summary>
    /// Generates a diagnostic report of the TCP listener state and metrics.
    /// This is a human-readable snapshot intended for troubleshooting and ops
    /// tooling, not a stable serialization format.
    /// </summary>
    /// <returns>A formatted string report.</returns>
    [Pure]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual string GenerateReport()
    {
        StringBuilder sb = new(2048);
        ThreadPool.GetMinThreads(out int minWorker, out int minIocp);

        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] TcpListenerBase Status:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Port                : {_port}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"StateWrapper        : {this.State}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Disposed            : {_isDisposed}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Configuration:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"EnableTimeout       : {_config.EnableTimeout}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"MaxParallelAccepts  : {_config.MaxParallel}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"BufferSize          : {_config.BufferSize}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"KeepAlive           : {_config.KeepAlive}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"ReuseAddress        : {_config.ReuseAddress}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"EnableDualStack     : {_config.EnableDualStack}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Backlog             : {_config.Backlog}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Metrics:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Accepted      : {this.Metrics.TotalAccepted}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Rejected      : {this.Metrics.TotalRejected}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - Queue Full      : {this.Metrics.TotalQueueFullRejections}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - Limiter/Guard   : {this.Metrics.TotalLimiterRejections}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Errors        : {this.Metrics.TotalErrors}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Proxy Protocol Errs : {this.Metrics.TotalProxyProtocolErrors}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Pending Proxies     : {Volatile.Read(ref _pendingProxyConnections)}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Accept Queue Depth  : {this.ProcessChannelCount}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Protocol:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"BoundProtocol       : {_protocol.ToString() ?? "-"}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Connections:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"ActiveConnections   : {_hub.Count}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Threading:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"ThreadPool MinWorker: {minWorker}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"ThreadPool MinIOCP  : {minIocp}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("--------------------------------------------");
        return sb.ToString();
    }

    /// <inheritdoc/>
    public virtual void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        ThreadPool.GetMinThreads(out int minWorker, out int minIocp);

        writer.WriteStartObject();
        writer.WriteString("UtcNow", DateTime.UtcNow);
        writer.WriteNumber("Port", _port);
        writer.WriteString(nameof(this.State), this.State.ToString());
        writer.WriteBoolean("Disposed", _isDisposed != 0);

        writer.WriteStartObject("Configuration");
        writer.WriteBoolean("EnableTimeout", _config.EnableTimeout);
        writer.WriteNumber("MaxParallelAccepts", _config.MaxParallel);
        writer.WriteNumber("BufferSize", _config.BufferSize);
        writer.WriteBoolean("KeepAlive", _config.KeepAlive);
        writer.WriteBoolean("ReuseAddress", _config.ReuseAddress);
        writer.WriteBoolean("EnableDualStack", _config.EnableDualStack);
        writer.WriteNumber("Backlog", _config.Backlog);
        writer.WriteEndObject();

        writer.WriteStartObject(nameof(this.Metrics));
        writer.WriteNumber("TotalAccepted", this.Metrics.TotalAccepted);
        writer.WriteNumber("TotalRejected", this.Metrics.TotalRejected);
        writer.WriteNumber("QueueFullRejections", this.Metrics.TotalQueueFullRejections);
        writer.WriteNumber("LimiterRejections", this.Metrics.TotalLimiterRejections);
        writer.WriteNumber("TotalErrors", this.Metrics.TotalErrors);
        writer.WriteNumber("ProxyProtocolErrors", this.Metrics.TotalProxyProtocolErrors);
        writer.WriteNumber("PendingProxyConnections", Volatile.Read(ref _pendingProxyConnections));
        writer.WriteNumber("AcceptQueueDepth", this.ProcessChannelCount);
        writer.WriteEndObject();

        writer.WriteStartObject(nameof(_protocol));
        writer.WriteString("BoundProtocol", _protocol?.ToString() ?? "-");
        writer.WriteEndObject();

        writer.WriteStartObject("Connections");
        writer.WriteNumber("ActiveConnections", _hub.Count);
        writer.WriteEndObject();

        writer.WriteStartObject("Threading");
        writer.WriteNumber("ThreadPoolMinWorker", minWorker);
        writer.WriteNumber("ThreadPoolMinIOCP", minIocp);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    #endregion IReportable Implementation
}
