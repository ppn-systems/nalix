// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Framework.Tasks;
using Nalix.Network.Connections;
using Nalix.Network.Internal.WebSockets;
using Nalix.Network.Listeners.Tcp;
using Nalix.Network.Options;

#pragma warning disable IDE0079
#pragma warning disable CA2213

namespace Nalix.Network.Listeners.Web;

/// <summary>
/// Provides a base implementation for a WebSocket listener using TcpListenerBase and raw socket accept.
/// </summary>
[SkipLocalsInit]
[DebuggerNonUserCode]
public abstract partial class WebSocketListenerBase : TcpListenerBase
{
    #region Fields

    private readonly string _path;
    private readonly byte[] _versionResponseBytes;
    private byte[]? _devOpsBearerToken;
    private readonly long _handshakeTimeoutTicks;

    private readonly NetworkWebSocketOptions _wsconfig;
    private readonly ForwardedHeadersOptions _forwardedConfig;
    private readonly HttpRouteTable _httpRoutes = new();

    // Intrusive linked list for WebSocket upgrade handshake timeout sweep
    private IRecurringHandle? _recurringHandle;
    private WebSocketUpgradeContext? _wsUpgradeHead;
    private WebSocketUpgradeContext? _wsUpgradeTail;

    private readonly Lock _wsUpgradeLock = new();
    private readonly ConcurrentStack<SocketAsyncEventArgs> _wsReceiveArgsPool = new();

    // The upgraded connections of this listener, so shutdown can close each one with a close frame.
    private readonly ConcurrentDictionary<WebSocketConnection, byte> _liveConnections = new();
    private readonly EventHandler<IConnectionEventArgs> _onLiveConnectionClosed;

    /// <summary>Upper bound for shutdown to wait on the closing handshakes of the live connections.</summary>
    private static readonly TimeSpan s_shutdownCloseTimeout = TimeSpan.FromSeconds(3);

    private const string ShutdownCloseReason = "Server shutting down";

    /// <summary>1 from the start of <see cref="Deactivate"/> until the next <see cref="Activate"/>.</summary>
    private int _shuttingDown;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketListenerBase"/> class using the configuration,
    /// and the specified protocol, buffer pool, and logger.
    /// </summary>
    /// <param name="port">The port number to listen on.</param>
    /// <param name="path">The HTTP path prefix to listen on.</param>
    /// <param name="protocol">The WebSocket protocol implementation.</param>
    /// <param name="hub">The connection hub to register active connections.</param>
    /// <param name="guard">The connection guard to limit resources.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="protocol"/> is <c>null</c>.</exception>
    [DebuggerStepThrough]
    protected WebSocketListenerBase(ushort port, string path, IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
        : base(port, protocol, hub, guard)
    {
        _path = string.IsNullOrEmpty(path) ? "/ws/" : path;
        _wsconfig = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        _forwardedConfig = ConfigurationManager.Instance.Get<ForwardedHeadersOptions>();
        _wsconfig.Validate();
        _versionResponseBytes = this.BUILD_VERSION_RESPONSE_BYTES();
        _handshakeTimeoutTicks = (long)(_wsconfig.HandshakeTimeoutMs / 1000.0 * Stopwatch.Frequency);
        _onLiveConnectionClosed = this.OnLiveConnectionClosed;
        this.REGISTER_DEFAULT_DEVOPS_ROUTES();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketListenerBase"/> class using the configuration,
    /// and the specified protocol, buffer pool, and logger.
    /// </summary>
    /// <param name="protocol">The WebSocket protocol implementation.</param>
    /// <param name="hub">The connection hub to register active connections.</param>
    /// <param name="guard">The connection guard to limit resources.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="protocol"/> is <c>null</c>.</exception>
    [DebuggerStepThrough]
    protected WebSocketListenerBase(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
        : base(protocol, hub, guard)
    {
        _wsconfig = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        _forwardedConfig = ConfigurationManager.Instance.Get<ForwardedHeadersOptions>();
        _path = string.IsNullOrEmpty(_wsconfig.Path) ? "/ws/" : _wsconfig.Path;
        _wsconfig.Validate();
        _versionResponseBytes = this.BUILD_VERSION_RESPONSE_BYTES();
        _handshakeTimeoutTicks = (long)(_wsconfig.HandshakeTimeoutMs / 1000.0 * Stopwatch.Frequency);
        _onLiveConnectionClosed = this.OnLiveConnectionClosed;
        this.REGISTER_DEFAULT_DEVOPS_ROUTES();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketListenerBase"/> class using the configuration,
    /// and the specified protocol, buffer pool, and logger.
    /// </summary>
    /// <param name="path">The HTTP path prefix to listen on.</param>
    /// <param name="protocol">The WebSocket protocol implementation.</param>
    /// <param name="hub">The connection hub to register active connections.</param>
    /// <param name="guard">The connection guard to limit resources.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="protocol"/> is <c>null</c>.</exception>
    [DebuggerStepThrough]
    protected WebSocketListenerBase(string path, IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
        : this(ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Port, path, protocol, hub, guard)
    {
    }

    #endregion Constructors

    #region Minimal HTTP Routing

    /// <summary>
    /// Registers or overrides a minimal HTTP <c>GET</c> route on this WebSocket listener port.
    /// </summary>
    /// <param name="path">The HTTP route path (e.g., <c>"/status"</c> or <c>"/metrics"</c>).</param>
    /// <param name="handler">The route handler delegate.</param>
    public void MapGet(string path, HttpRouteHandler handler) => _httpRoutes.Register(HttpMethodType.Get, path, handler);

    /// <summary>
    /// Registers or overrides a minimal HTTP <c>POST</c> route on this WebSocket listener port.
    /// </summary>
    /// <param name="path">The HTTP route path (e.g., <c>"/webhook"</c>).</param>
    /// <param name="handler">The route handler delegate.</param>
    public void MapPost(string path, HttpRouteHandler handler) => _httpRoutes.Register(HttpMethodType.Post, path, handler);

    #endregion Minimal HTTP Routing

    #region Lifecycle

    /// <inheritdoc/>
    public override void Activate(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _shuttingDown, 0);
        base.Activate(cancellationToken);

        // IsRunning is only true while a sweep callback executes, so it cannot tell whether the job is scheduled.
        if (_recurringHandle != null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_wsconfig.AllowedOrigins) &&
            DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog(
                "NW.ws:origin",
                $"ws-origin-check-disabled port={_config.Port} NetworkWebSocketOptions.AllowedOrigins is empty; any browser origin may open a WebSocket (CSWSH risk). Set AllowedOrigins for browser-facing deployments."));
        }

        // Start the WebSocket handshake timeout sweeper (fire and forget, as requested)
        _recurringHandle = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleRecurring(
            name: $"{TaskNaming.Tags.WebSocket}.{TaskNaming.Tags.Service}.{_port}",
            interval: TimeSpan.FromSeconds(1),
            work: _ => { this.SWEEP_WS_HANDSHAKE_TIMEOUTS(); return ValueTask.CompletedTask; }
        );
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Live connections are closed first with a <see cref="WebSocketCloseStatus.EndpointUnavailable"/> (1001) close
    /// frame, before the listener's cancellation would drop their sockets.
    /// </remarks>
    public override void Deactivate(CancellationToken cancellationToken = default)
    {
        // Published before the live connections are read: an upgrade finishing now either is among them or sees
        // the flag and closes its connection itself (see TrackLiveConnection).
        _ = Interlocked.Exchange(ref _shuttingDown, 1);
        this.CLOSE_LIVE_CONNECTIONS();

        base.Deactivate(cancellationToken);

        // Disposing the handle only cancels it; CancelRecurring also frees the name so the listener can be
        // activated again (or another listener can take the same port) without a duplicate-name error.
        if (_recurringHandle != null)
        {
            InstanceManager.Instance.GetOrCreateInstance<TaskManager>().CancelRecurring(_recurringHandle.Name);
            _recurringHandle = null;
        }

        this.CLEANUP_WS_UPGRADES();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.CLEANUP_WS_UPGRADES();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Adds an upgraded connection to the ones <see cref="Deactivate"/> closes. Returns <see langword="false"/> when
    /// the listener is shutting down: the connection is then closed here, with the same 1001 close frame, because
    /// <see cref="Deactivate"/> may already have gone over the live connections. (The 101 response, which completes
    /// the client's connect, goes out before the connection is tracked.)
    /// </summary>
    internal bool TrackLiveConnection(WebSocketConnection connection)
    {
        _ = _liveConnections.TryAdd(connection, 0);
        connection.ConnectionClosed += _onLiveConnectionClosed;

        Interlocked.MemoryBarrier(); // The add above, then the flag: pairs with the exchange in Deactivate.
        if (Volatile.Read(ref _shuttingDown) == 0)
        {
            return true;
        }

        _ = connection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, ShutdownCloseReason);
        return false;
    }

    private void OnLiveConnectionClosed(object? sender, IConnectionEventArgs args)
    {
        if (sender is WebSocketConnection connection)
        {
            _ = _liveConnections.TryRemove(connection, out _);
        }
    }

    private void CLOSE_LIVE_CONNECTIONS()
    {
        if (_liveConnections.IsEmpty)
        {
            return;
        }

        List<Task> closing = [];
        foreach (WebSocketConnection connection in _liveConnections.Keys)
        {
            closing.Add(connection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, ShutdownCloseReason));
        }

        _liveConnections.Clear();

        // Each closing handshake is bounded on its own; this only stops shutdown from waiting on them forever.
        _ = Task.WhenAll(closing).Wait(s_shutdownCloseTimeout);
    }

    #endregion Lifecycle
}
