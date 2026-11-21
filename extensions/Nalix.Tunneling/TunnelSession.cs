// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;

namespace Nalix.Tunneling;

/// <summary>
/// Manages the lifecycle of a tunneled connection, handling transition from Nalix to raw socket piping.
/// </summary>
public sealed class TunnelSession : IAsyncDisposable
{
    private readonly ILogger? _logger;
    private readonly ulong _connectionId;
    private readonly TunnelOptions _options;
    private readonly CancellationTokenSource _cts;
    private readonly IConnection _consumerConnection;
    private readonly TunnelSessionRegistry? _registry;

    private int _disposed;
    private Task? _pipeTask;

    public TunnelSession(IConnection consumerConnection, TunnelOptions options, TunnelSessionRegistry? registry = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(consumerConnection);

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cts = new();
        _consumerConnection = consumerConnection;
        _connectionId = consumerConnection.ConnectionId;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Transitions the connection to the provider data connection.
    /// Unwraps both sockets and spawns the bi-directional raw socket bridge.
    /// </summary>
    public void StartTunnel(IConnection providerDataConnection)
    {
        ArgumentNullException.ThrowIfNull(providerDataConnection);

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Detach raw socket from Nalix engine
        _consumerConnection.ExcludeFromIdleTimeout = true;
        providerDataConnection.ExcludeFromIdleTimeout = true;

        if (_consumerConnection.Transport is not IConnection.ISocketTransport consumerSocketTransport)
        {
            throw new NotSupportedException("Consumer connection does not support socket unwrapping.");
        }

        if (providerDataConnection.Transport is not IConnection.ISocketTransport providerSocketTransport)
        {
            throw new NotSupportedException("Provider connection does not support socket unwrapping.");
        }

        Socket consumerSocket = consumerSocketTransport.Unwrap();
        Socket providerSocket = providerSocketTransport.Unwrap();

        // Start bi-directional piping and recover stolen data
        _pipeTask = this.StartTunnelAndCleanupAsync(consumerSocket, providerSocket, providerDataConnection);
    }

    private static async Task RecoverStolenDataAsync(IConnection sourceConnection, Socket destinationSocket)
    {
        if (sourceConnection.Transport is IConnection.ISocketTransport transport)
        {
            try
            {
                if (transport.ReceiveLoopTask is not null)
                {
                    await transport.ReceiveLoopTask.ConfigureAwait(false);
                }

                byte[]? stolen = transport.StolenData;
                if (stolen != null && stolen.Length > 0)
                {
                    _ = await destinationSocket.SendAsync(stolen, SocketFlags.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                // Ignore exceptions during recovery
            }
            finally
            {
                sourceConnection.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }

        if (_pipeTask is not null)
        {
            try
            {
                await _pipeTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex)) { }
        }

        if (_registry is not null)
        {
            _ = _registry.TryRemove(_connectionId, out _);
        }

        _cts.Dispose();
        _consumerConnection.Dispose();
    }

    private async Task StartTunnelAndCleanupAsync(Socket consumerSocket, Socket providerSocket, IConnection providerDataConnection)
    {
        try
        {
            // Recover stolen packets from the sockets concurrently and wait for them to finish before piping starts
            Task recoverConsumer = RecoverStolenDataAsync(_consumerConnection, providerSocket);
            Task recoverProvider = RecoverStolenDataAsync(providerDataConnection, consumerSocket);
            await Task.WhenAll(recoverConsumer, recoverProvider).ConfigureAwait(false);

            await TunnelPipe.StartAsync(consumerSocket, providerSocket, _options, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[Tunneling.TunnelSession] ended connection={ConnectionId} reason=socket-exception", _connectionId);
            }
        }
        finally
        {
            await this.DisposeAsync().ConfigureAwait(false);
        }
    }
}
