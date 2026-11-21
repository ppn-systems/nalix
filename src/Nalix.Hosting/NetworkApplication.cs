// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nalix.Abstractions;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Framework;
using Nalix.Framework.Injection;
using Nalix.Framework.Tasks;
using Nalix.Hosting.Internal;
using Nalix.Hosting.Protocols;
using Nalix.Runtime.Dispatching;

#pragma warning disable NALIX040 // NetworkApplicationBuilder should configure BufferPoolManager
#pragma warning disable NALIX041 // NetworkApplicationBuilder should configure ConnectionHub

namespace Nalix.Hosting;

/// <summary>
/// Represents a runnable host for Nalix TCP servers.
/// </summary>
/// <remarks>
/// Use <see cref="CreateBuilder"/> to configure a host instance, then call
/// <see cref="ActivateAsync(CancellationToken)"/>, <see cref="RunAsync(CancellationToken)"/>,
/// or the lifecycle methods inherited from <see cref="IActivatable"/> and
/// <see cref="IActivatableAsync"/>.
/// </remarks>
public sealed class NetworkApplication : IActivatableAsync, IAsyncDisposable
{
    #region Fields

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    private readonly Action _prepareCallbacks;
    private readonly Func<IPacketDispatch> _dispatchFactory;
    private readonly IReadOnlyList<Func<IPacketDispatch, ListenerBinding>> _serverFactories;

    private readonly List<IListener> _listeners = [];
    private readonly List<IProtocol> _protocols = [];

    private bool _isStarted;
    private bool _isDisposed;
    private IPacketDispatch? _packetDispatch;

    #endregion Fields

    #region Constructors

    internal NetworkApplication(
        ILogger logger,
        Action prepareCallbacks,
        Func<IPacketDispatch> dispatchFactory,
        IReadOnlyList<Func<IPacketDispatch, ListenerBinding>> serverFactories)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _prepareCallbacks = prepareCallbacks ?? throw new ArgumentNullException(nameof(prepareCallbacks));
        _dispatchFactory = dispatchFactory ?? throw new ArgumentNullException(nameof(dispatchFactory));
        _serverFactories = serverFactories ?? throw new ArgumentNullException(nameof(serverFactories));
    }

    #endregion Constructors

    #region APIs

    /// <summary>
    /// Creates a minimal <see cref="NetworkApplication"/> with sensible defaults:
    /// <list type="bullet">
    ///   <item>Default logger</item>
    ///   <item>Default ConnectionHub + BufferPoolManager</item>
    ///   <item>Default TCP binding on port from configuration (or 8080)</item>
    ///   <item>Automatic handler scanning from calling assembly</item>
    /// </list>
    /// </summary>
    /// <param name="port">Optional port override. If null, uses configuration or defaults to 8080.</param>
    /// <returns>A ready-to-run minimal NetworkApplication.</returns>
    public static NetworkApplication CreateMinimal(ushort? port = null)
    {
        NetworkApplicationBuilder builder = CreateBuilder();

        // Default TCP binding with DefaultProtocol
        IProtocolBindingBuilder tcpBuilder = builder.MapTcp<DefaultProtocol>();

        if (port.HasValue)
        {
            _ = tcpBuilder.OnPort(port.Value);
        }

        _ = tcpBuilder.Bind();

        // Note: Assembly scanning has been removed for Native AOT compatibility.
        // Handlers are discovered via source-generated PacketHandlerRegistry
        // or registered explicitly via MapHandlers<T>().

#pragma warning disable NALIX044 // CreateMinimal binds the default TCP protocol through MapTcp<T>().Bind() above.
        NetworkApplication application = builder.Build();
#pragma warning restore NALIX044
        return application;
    }

    /// <summary>
    /// Creates a new builder for <see cref="NetworkApplication"/>.
    /// </summary>
    /// <returns>A new <see cref="NetworkApplicationBuilder"/> instance.</returns>
    public static NetworkApplicationBuilder CreateBuilder() => new(new HostingBuilderContext());

    /// <summary>
    /// Runs the host until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">A token that stops the host when canceled.</param>
    /// <returns>A task that completes when the host has stopped.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await this.ActivateAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await this.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isStarted)
            {
                return;
            }

            _prepareCallbacks();

            if (_packetDispatch is not null)
            {
                try { _packetDispatch.Deactivate(cancellationToken); }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
                _packetDispatch = null;
            }

            _packetDispatch = _dispatchFactory();

            try
            {
                InstanceManager.Instance.Register<IPacketDispatch>(_packetDispatch);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                _logger.LogDebug(ex, "IPacketDispatch registration replaced existing instance.");
            }

            _packetDispatch.Activate(cancellationToken);

            try
            {
                for (int i = 0; i < _serverFactories.Count; i++)
                {
                    ListenerBinding server = _serverFactories[i](_packetDispatch);

                    if (server.Protocol is not null)
                    {
                        _protocols.Add(server.Protocol);
                    }

                    _listeners.Add(server.Listener);

                    if (server.Transport is NetworkTransport transport)
                    {
                        ReportRegistry.Instance.Register<IListener>(transport, server.Listener);

                        if (server.Protocol is not null)
                        {
                            ReportRegistry.Instance.Register<IProtocol>(transport, server.Protocol);
                        }
                    }

                    server.Listener.Activate(cancellationToken);
                }
            }
            catch
            {
                this.CleanupPartialActivation(cancellationToken);
                throw;
            }

            _isStarted = true;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private void CleanupPartialActivation(CancellationToken cancellationToken)
    {
        for (int i = _listeners.Count - 1; i >= 0; i--)
        {
            try { _listeners[i].Deactivate(cancellationToken); }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
            try { _listeners[i].Dispose(); }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
        }
        _listeners.Clear();

        for (int i = _protocols.Count - 1; i >= 0; i--)
        {
            try { _protocols[i].Dispose(); }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
        }
        _protocols.Clear();

        try { _packetDispatch?.Deactivate(cancellationToken); }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
        _packetDispatch = null;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isStarted)
            {
                return;
            }

            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                try
                {
                    _listeners[i].Deactivate(cancellationToken);
                    _listeners[i].Dispose();
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                }
            }

            _listeners.Clear();

            for (int i = _protocols.Count - 1; i >= 0; i--)
            {
                try
                {
                    _protocols[i].Dispose();
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                }
            }

            _protocols.Clear();

            try
            {
                _packetDispatch?.Deactivate(cancellationToken);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                }
            }

            _packetDispatch = null;

            _isStarted = false;

            // BUG-Fix: Ensure all background workers are fully stopped before returning.
            // Without this, "zombie" tasks from Test A might interfere with Test B's resources.
            ITaskManager? taskManager = InstanceManager.Instance.GetExistingInstance<ITaskManager>();

            if (taskManager is not null)
            {
                // Wait for all network and time-related workers (listeners, dispatchers, timing wheels, etc.)
                // These groups usually start with 'net/' or 'time/'
                try
                {
                    await InstanceManager.Instance.GetOrCreateInstance<TaskManager>().WaitGroupAsync("net/*", cancellationToken).ConfigureAwait(false);
                    await InstanceManager.Instance.GetOrCreateInstance<TaskManager>().WaitGroupAsync("time/*", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    _logger.LogWarning(ex, "Failed to wait for background workers during shutdown.");
                }
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, true))
        {
            return;
        }

        try
        {
            await this.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to stop Nalix application during dispose.");
            }
        }

        _gate.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, true))
        {
            return;
        }

        Task deactivateTask = this.DeactivateAsync(CancellationToken.None);

        if (deactivateTask.IsCompleted)
        {
            if (deactivateTask.Exception?.GetBaseException() is Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Failed to stop Nalix application during dispose.");
                }
            }
        }
        else
        {
            try
            {
                deactivateTask.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Failed to stop Nalix application during dispose.");
                }
            }
        }

        _gate.Dispose();
    }

    #endregion APIs
}
