// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;

namespace Nalix.Tunneling;

/// <summary>
/// Thread-safe registry that tracks all active <see cref="TunnelSession"/> instances.
/// </summary>
public sealed class TunnelSessionRegistry
{
    private readonly ConcurrentDictionary<ulong, TunnelSession> _sessions = new();
    private readonly ILogger? _logger;

    public TunnelSessionRegistry(ILogger? logger = null) => _logger = logger;

    public int Count => _sessions.Count;

    public bool Contains(ulong connectionId) => _sessions.ContainsKey(connectionId);

    public void Register(ulong connectionId, TunnelSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!_sessions.TryAdd(connectionId, session))
        {
            throw new InvalidOperationException($"An active tunnel session already exists for connection '{connectionId}'.");
        }
    }

    public bool TryRemove(ulong connectionId, out TunnelSession? session) => _sessions.TryRemove(connectionId, out session);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "DisposeAsync is called in the finally block; ownership is transferred from TryRemove.")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "async void event handler must never throw — any exception would crash the process.")]
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Event handler signature")]
    public async void OnConnectionClosed(object? sender, IConnectionEventArgs args)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(args);

            ulong connectionId = args.Connection.ConnectionId;

            if (!_sessions.TryRemove(connectionId, out TunnelSession? session))
            {
                return;
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "[Tunneling.TunnelSessionRegistry] dispose-error connection={ConnectionId}", connectionId);
                }
            }
        }
        catch (Exception)
        {
            // async void must never propagate exceptions
        }
    }
}
