// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Random;
using Nalix.Framework.Injection;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Network.Connections;

namespace Nalix.Network.Listeners.Datagram;

public sealed partial class DatagramListener
{
    #region IListener

    /// <summary>
    /// Binds the socket and starts the receive workers and the ticket sweep.
    /// </summary>
    /// <param name="cancellationToken">Stops the listener when cancelled.</param>
    /// <exception cref="ObjectDisposedException">The listener is disposed.</exception>
    /// <exception cref="SocketException">The socket cannot be bound.</exception>
    public void Activate(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        // Avoid blocking lifecycle transitions behind a concurrent caller.
        if (!_lock.Wait(0, CancellationToken.None))
        {
            return;
        }

        try
        {
            if (this.State != ListenerState.STOPPED)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.DatagramListener:Activate", $"ignored-activate state={this.State}"));
                }

                return;
            }

            _ = Interlocked.Exchange(ref _state, (int)ListenerState.STARTING);

            _socket = this.CreateSocket();
            Socket socket = _socket;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _cts.Token;

            TaskManager tasks = InstanceManager.Instance.GetOrCreateInstance<TaskManager>();
            IWorkerHandle[] workers = new IWorkerHandle[_options.ReceiveWorkers];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = tasks.ScheduleWorker(
                    name: $"{TaskNaming.Tags.Datagram}.{TaskNaming.Tags.Accept}.{i}",
                    group: $"{TaskNaming.Tags.Net}/{TaskNaming.Tags.Datagram}/{this.Port}",
                    work: (ctx, ct) => this.ReceiveAsync(socket, ctx, ct),
                    options: new WorkerOptions
                    {
                        Tag = TaskNaming.Tags.Net,
                        IdType = SnowflakeType.System,
                        CancellationToken = token,
                        RetainFor = TimeSpan.FromSeconds(30),
                    });
            }

            _receiveWorkers = workers;
            _timingWheel.Activate(token);
            _sweepJob = tasks.ScheduleRecurring(
                name: $"{TaskNaming.Tags.Datagram}.Tickets.Sweep.{this.Port}",
                interval: TimeSpan.FromSeconds(1),
                work: _ =>
                {
                    this.SweepExpiredTickets();
                    return ValueTask.CompletedTask;
                },
                options: new RecurringOptions
                {
                    Tag = TaskNaming.Tags.Net,
                    NonReentrant = true,
                });

            _ = Interlocked.Exchange(ref _state, (int)ListenerState.RUNNING);

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information,
                    new DiagnosticLog("NW.DatagramListener:Activate", $"start protocol={_protocol.GetType().Name} port={this.Port} workers={workers.Length}"));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Critical))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Critical,
                    new DiagnosticLog("NW.DatagramListener:Activate", $"start-failed port={_port}", ex));
            }

            this.ReleaseResources();
            _ = Interlocked.Exchange(ref _state, (int)ListenerState.STOPPED);
            throw;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <summary>
    /// Stops receiving, closes every session and discards the pending tickets.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for an activation in progress.</param>
    public void Deactivate(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, (int)ListenerState.STOPPING, (int)ListenerState.RUNNING) != (int)ListenerState.RUNNING)
        {
            if (this.State != ListenerState.STARTING)
            {
                return;
            }

            // Activate holds the lock while STARTING: wait for it to finish, then stop what it started.
            _lock.Wait(cancellationToken);
            _ = _lock.Release();

            if (Interlocked.CompareExchange(ref _state, (int)ListenerState.STOPPING, (int)ListenerState.RUNNING) != (int)ListenerState.RUNNING)
            {
                return;
            }
        }

        // Sessions send their CLOSE through the socket, so close them before the socket goes away.
        foreach (DatagramConnection connection in _sessions.Values)
        {
            connection.Disconnect("Server is shutting down.");
        }

        foreach (PendingTicket ticket in _pending.Values)
        {
            ticket.Discard();
        }

        _pending.Clear();
        _ticketByStream.Clear();
        this.ReleaseResources();
        _ = Interlocked.Exchange(ref _state, (int)ListenerState.STOPPED);

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information,
                new DiagnosticLog("NW.DatagramListener:Deactivate", $"stopped port={_port}"));
        }
    }

    #endregion IListener

    #region IDatagramTicketIssuer

    /// <inheritdoc/>
    public DatagramTicketInfo IssueTicket(IConnection streamConnection, Action<IConnection>? onAccepted = null)
    {
        ArgumentNullException.ThrowIfNull(streamConnection);

        if (!this.IsListening)
        {
            throw new InvalidOperationException("The datagram listener is not active.");
        }

        if (streamConnection is DatagramConnection)
        {
            throw new InvalidOperationException("A datagram session cannot issue a datagram ticket.");
        }

        if (streamConnection.Secret.IsZero)
        {
            throw new InvalidOperationException("The stream connection has no session secret; complete the handshake first.");
        }

        Span<byte> nonce = stackalloc byte[16];
        Csprng.Fill(nonce);
        UInt128 nonceValue = new(BitConverter.ToUInt64(nonce[8..]), BitConverter.ToUInt64(nonce));

        PendingTicket ticket = new(
            new DatagramIdentity(streamConnection.UserId, streamConnection.Level, streamConnection.Algorithm, streamConnection.Secret, streamConnection.ConnectionId),
            nonce.ToArray(),
            System.Environment.TickCount64 + (_options.TicketLifetimeSeconds * 1000L),
            onAccepted,
            streamConnection.ConnectionId);
        nonce.Clear();

        ulong connectionId;
        lock (_ticketLock)
        {
            // One outstanding ticket per stream connection: a new request replaces the previous ticket, so a client
            // cannot fill the session table by asking in a loop.
            if (_ticketByStream.TryGetValue(streamConnection.ConnectionId, out ulong previous) &&
                _pending.TryGetValue(previous, out PendingTicket? replaced))
            {
                _ = this.RemoveTicket(previous, replaced);
            }

            // Checked and added under the lock, so concurrent requests cannot overshoot the limit.
            if (_sessions.Count + _pending.Count >= _options.MaxSessions)
            {
                ticket.Discard();
                throw new InvalidOperationException("The datagram session limit is reached.");
            }

            do
            {
                // Not a Snowflake: the identifier must not be predictable.
                connectionId = Csprng.NextUInt64();
            }
            while (connectionId == 0 || _sessions.ContainsKey(connectionId) || !_pending.TryAdd(connectionId, ticket));

            _ticketByStream[streamConnection.ConnectionId] = connectionId;
        }

        this.Metrics.RECORD_TICKET_ISSUED();

        return new DatagramTicketInfo(
            connectionId, nonceValue, this.Port, (ushort)_options.TicketLifetimeSeconds, _engineOptions.IdleTimeoutMs,
            _engineOptions.MaxMessageSize, (byte)_engineOptions.ChannelCount, _engineOptions.ReceiveBufferBytes, _engineOptions.ChannelReceiveBufferBytes);
    }

    #endregion IDatagramTicketIssuer

    #region IReportable

    /// <summary>
    /// Generates a human-readable snapshot of the listener state and metrics.
    /// </summary>
    [Pure]
    public string GenerateReport()
    {
        StringBuilder sb = new(1024);

        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] DatagramListener Status:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Port                : {this.Port}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"State               : {this.State}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Disposed            : {Volatile.Read(ref _isDisposed) != 0}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Configuration:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"ReceiveWorkers      : {_options.ReceiveWorkers}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"MaxSessions         : {_options.MaxSessions}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"TicketLifetime (s)  : {_options.TicketLifetimeSeconds}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"SocketRcvBuf        : {_options.SocketReceiveBufferSize}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"SocketSndBuf        : {_options.SocketSendBufferSize}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"IdleTimeout (ms)    : {_engineOptions.IdleTimeoutMs}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Channels            : {_engineOptions.ChannelCount}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Sessions:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Established         : {_sessions.Count}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Pending Tickets     : {_pending.Count}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Tickets Issued      : {this.Metrics.TicketsIssued}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Tickets Expired     : {this.Metrics.TicketsExpired}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Accepted      : {this.Metrics.TotalAccepted}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Guard Rejections    : {this.Metrics.TotalGuardRejections}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Traffic:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Datagrams Received  : {this.Metrics.DatagramsReceived}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Bytes Received      : {this.Metrics.BytesReceived}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Dropped             : {this.Metrics.TotalDropped}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - Malformed       : {this.Metrics.DroppedMalformed}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - Unknown CID     : {this.Metrics.DroppedUnknown}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  - Unauthenticated : {this.Metrics.DroppedUnauthenticated}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Errors              : {this.Metrics.TotalErrors}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Timers:");
        _ = sb.AppendLine("--------------------------------------------");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Wheel Running       : {_timingWheel.IsRunning}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Deadlines Fired     : {_timingWheel.FiredCount}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Stale Entries       : {_timingWheel.StaleSkipCount}");
        _ = sb.AppendLine("--------------------------------------------");

        return sb.ToString();
    }

    /// <inheritdoc/>
    public void WriteReportData(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString("UtcNow", DateTime.UtcNow);
        writer.WriteNumber(nameof(this.Port), this.Port);
        writer.WriteString(nameof(this.State), this.State.ToString());
        writer.WriteBoolean("Disposed", Volatile.Read(ref _isDisposed) != 0);
        writer.WriteNumber("Sessions", _sessions.Count);
        writer.WriteNumber("PendingTickets", _pending.Count);

        writer.WriteStartObject(nameof(this.Metrics));
        writer.WriteNumber(nameof(DMetrics.TicketsIssued), this.Metrics.TicketsIssued);
        writer.WriteNumber(nameof(DMetrics.TicketsExpired), this.Metrics.TicketsExpired);
        writer.WriteNumber(nameof(DMetrics.TotalAccepted), this.Metrics.TotalAccepted);
        writer.WriteNumber(nameof(DMetrics.TotalGuardRejections), this.Metrics.TotalGuardRejections);
        writer.WriteNumber(nameof(DMetrics.DatagramsReceived), this.Metrics.DatagramsReceived);
        writer.WriteNumber(nameof(DMetrics.BytesReceived), this.Metrics.BytesReceived);
        writer.WriteNumber(nameof(DMetrics.DroppedMalformed), this.Metrics.DroppedMalformed);
        writer.WriteNumber(nameof(DMetrics.DroppedUnknown), this.Metrics.DroppedUnknown);
        writer.WriteNumber(nameof(DMetrics.DroppedUnauthenticated), this.Metrics.DroppedUnauthenticated);
        writer.WriteNumber(nameof(DMetrics.TotalErrors), this.Metrics.TotalErrors);
        writer.WriteEndObject();

        writer.WriteStartObject("Timers");
        writer.WriteBoolean("Running", _timingWheel.IsRunning);
        writer.WriteNumber("Fired", _timingWheel.FiredCount);
        writer.WriteNumber("StaleSkips", _timingWheel.StaleSkipCount);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    #endregion IReportable

    #region Private Methods

    /// <summary>Stops the workers and the sweep, cancels and closes the socket.</summary>
    private void ReleaseResources()
    {
        Interlocked.Exchange(ref _sweepJob, null)?.Dispose();
        _timingWheel.Deactivate();

        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // Closing the socket ends a ReceiveFromAsync that ignores the token.
        Interlocked.Exchange(ref _socket, null)?.Dispose();

        IWorkerHandle[]? workers = Interlocked.Exchange(ref _receiveWorkers, null);
        if (workers is not null)
        {
            foreach (IWorkerHandle worker in workers)
            {
                worker.Dispose();
            }
        }

        cts?.Dispose();
    }

    #endregion Private Methods
}
