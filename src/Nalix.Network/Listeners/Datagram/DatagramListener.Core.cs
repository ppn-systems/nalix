// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Configuration;
using Nalix.Environment.Options;
using Nalix.Framework.Tasks;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.Options;

namespace Nalix.Network.Listeners.Datagram;

/// <summary>
/// UDP listener of the Nalix Datagram Protocol (NDP). It issues tickets to established stream connections
/// (<see cref="IDatagramTicketIssuer"/>), routes each datagram to its session by connection identifier, and turns
/// a ticket into a <see cref="DatagramConnection"/> when its first datagram authenticates.
/// </summary>
/// <remarks>
/// <para>
/// Unauthenticated datagrams are never answered: an unknown connection identifier costs one table lookup, and only
/// a known one reaches the AEAD. Crypto is injected through an <see cref="IDatagramProtectionFactory"/>, so this
/// assembly does not reference the codec.
/// </para>
/// <para>
/// A session counts as a connection for the <see cref="IConnectionGuard"/> (blacklist and per-IP limits) from the
/// moment its first datagram authenticates until it closes.
/// </para>
/// </remarks>
[DebuggerDisplay("Port={Port}, State={State}, Sessions={SessionCount}")]
public sealed partial class DatagramListener : IListener, IDatagramTicketIssuer
{
    #region Enums

    private enum ListenerState
    {
        STOPPED = 0,
        STARTING = 1,
        RUNNING = 2,
        STOPPING = 3,
    }

    #endregion Enums

    #region Fields

    private readonly ushort _port;
    private readonly IProtocol _protocol;
    private readonly IConnectionHub _hub;
    private readonly IConnectionGuard? _guard;
    private readonly IDatagramProtectionFactory _protectionFactory;
    private readonly DatagramOptions _options;
    private readonly DatagramEngineOptions _engineOptions;
    private readonly NetworkSocketOptions _socketOptions;
    private readonly ConcurrentDictionary<ulong, DatagramConnection> _sessions = new();
    private readonly ConcurrentDictionary<ulong, PendingTicket> _pending = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly DatagramTimingWheel _timingWheel;

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private IWorkerHandle[]? _receiveWorkers;
    private IRecurringHandle? _sweepJob;
    private int _state;
    private int _isDisposed;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Initializes the listener.
    /// </summary>
    /// <param name="port">The UDP port; 0 picks a free port (see <see cref="Port"/>).</param>
    /// <param name="protocol">The protocol whose frame processor receives the messages of every session.</param>
    /// <param name="hub">The hub sessions are registered in.</param>
    /// <param name="protectionFactory">Creates the AEAD protection of each session.</param>
    /// <param name="guard">Admission control for accepted sessions; <see langword="null"/> accepts every authenticated session.</param>
    /// <param name="options">Listener settings; defaults to the configured <see cref="DatagramOptions"/>.</param>
    /// <param name="engineOptions">Session settings advertised in tickets; defaults to the configured <see cref="DatagramEngineOptions"/>.</param>
    public DatagramListener(
        ushort port, IProtocol protocol, IConnectionHub hub, IDatagramProtectionFactory protectionFactory,
        IConnectionGuard? guard = null, DatagramOptions? options = null, DatagramEngineOptions? engineOptions = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(protectionFactory);

        _port = port;
        _protocol = protocol;
        _hub = hub;
        _guard = guard;
        _protectionFactory = protectionFactory;
        _options = options ?? ConfigurationManager.Instance.Get<DatagramOptions>();
        _engineOptions = engineOptions ?? ConfigurationManager.Instance.Get<DatagramEngineOptions>();
        _socketOptions = ConfigurationManager.Instance.Get<NetworkSocketOptions>();
        _timingWheel = new DatagramTimingWheel($"{TaskNaming.Tags.Datagram}.timers.{port}");

        _options.Validate();
        _engineOptions.Validate();

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog("NW.DatagramListener:Ctor", $"created port={_port} protocol={protocol.GetType().Name}"));
        }
    }

    #endregion Constructors

    #region Properties

    /// <summary>Gets the listener metrics.</summary>
    public DMetrics Metrics { get; } = new();

    /// <summary>Gets the bound UDP port, or the configured one before activation.</summary>
    public ushort Port => Volatile.Read(ref _socket)?.LocalEndPoint is IPEndPoint endPoint ? (ushort)endPoint.Port : _port;

    /// <summary>Gets a value indicating whether the listener is receiving datagrams.</summary>
    public bool IsListening => this.State == ListenerState.RUNNING;

    /// <summary>Gets the number of established sessions.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>Gets the number of tickets waiting for their first authenticated datagram.</summary>
    public int PendingTicketCount => _pending.Count;

    /// <summary>Gets the wheel that drives the protocol timers of the sessions.</summary>
    internal DatagramTimingWheel TimingWheel => _timingWheel;

    private ListenerState State => (ListenerState)Volatile.Read(ref _state);

    #endregion Properties

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        this.Deactivate();

        // Deactivate only releases a running listener; a failed or never-started one may still hold these.
        _sweepJob?.Dispose();
        _cts?.Dispose();
        _socket?.Dispose();
        _lock.Dispose();
    }

    #endregion IDisposable
}
