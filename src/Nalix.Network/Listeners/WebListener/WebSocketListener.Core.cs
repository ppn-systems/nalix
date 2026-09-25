// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Framework.Tasks;
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
    private readonly long _handshakeTimeoutTicks;

    private readonly NetworkWebSocketOptions _wsconfig;
    private readonly ForwardedHeadersOptions _forwardedConfig;

    // Intrusive linked list for WebSocket upgrade handshake timeout sweep
    private IRecurringHandle? _recurringHandle;
    private WebSocketUpgradeContext? _wsUpgradeHead;
    private WebSocketUpgradeContext? _wsUpgradeTail;

    private readonly Lock _wsUpgradeLock = new();
    private readonly ConcurrentStack<SocketAsyncEventArgs> _wsReceiveArgsPool = new();

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

    #region Lifecycle

    /// <inheritdoc/>
    public override void Activate(CancellationToken cancellationToken = default)
    {
        base.Activate(cancellationToken);

        if (_recurringHandle != null && _recurringHandle.IsRunning)
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
            name: $"{TaskNaming.Tags.WebSocket}.{TaskNaming.Tags.Service}.{_config.Port}",
            interval: TimeSpan.FromSeconds(1),
            work: _ => { this.SWEEP_WS_HANDSHAKE_TIMEOUTS(); return ValueTask.CompletedTask; }
        );
    }

    /// <inheritdoc/>
    public override void Deactivate(CancellationToken cancellationToken = default)
    {
        base.Deactivate(cancellationToken);

        if (_recurringHandle?.IsRunning == true)
        {
            _recurringHandle.Dispose();
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

    #endregion Lifecycle
}
