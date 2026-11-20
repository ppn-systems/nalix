// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal;

namespace Nalix.Network.Connections;

/// <summary>
/// Provides event data for connection-related events.
/// </summary>
/// <remarks>
/// This class is sealed to prevent derivation and ensure consistent behavior for connection event arguments.
/// </remarks>
public sealed class ConnectionEventArgs : EventArgs, IConnectionEventArgs, IPoolable, IPoolRentable
{
    #region Fields

    private IConnection? _connection;
    private int _returnedToPool;
    private IBufferLease? _lease;

    #endregion Fields

    #region Properties

    /// <inheritdoc/>
    public IBufferLease? Lease => _lease;

    /// <inheritdoc />
    [AllowNull]
    public IConnection Connection
    {
        get
        {
            if (_connection is null)
            {
                Throw.ConnectionNotAvailable();
            }

            return _connection;
        }
    }

    /// <inheritdoc />
    public INetworkEndpoint NetworkEndpoint => this.Connection.NetworkEndpoint;

    #endregion Properties

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionEventArgs"/> class with the specified connection.
    /// </summary>
    public ConnectionEventArgs()
    {
    }

    #endregion Constructors

    #region APIs

    /// <inheritdoc />
    internal void Initialize(IConnection connection)
    {
        _lease?.Dispose();
        _lease = null;
        _connection = connection ?? throw new ArgumentNullException(nameof(connection), "Connection cannot be null when initializing ConnectionEventArgs");
    }

    /// <inheritdoc />
    internal void Initialize([Borrowed] IBufferLease lease, IConnection connection)
    {
        if (!ReferenceEquals(_lease, lease))
        {
            _lease?.Dispose();
        }

        _lease = lease ?? throw new ArgumentNullException(nameof(lease), "Buffer lease cannot be null when initializing ConnectionEventArgs with a buffer");
        _connection = connection ?? throw new ArgumentNullException(nameof(connection), "Connection cannot be null when initializing ConnectionEventArgs with a buffer");
    }

    /// <inheritdoc />
    public IBufferLease? ExchangeLease([Borrowed] IBufferLease? newLease)
    {
        IBufferLease? old = _lease;
        _lease = newLease;
        return old;
    }

    /// <inheritdoc />
    public void OnRent() => _ = Interlocked.Exchange(ref _returnedToPool, 0);

    /// <inheritdoc />
    public void ResetForPool()
    {
        _lease?.Dispose();

        _lease = null;
        _connection = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returnedToPool, 1) != 0)
        {
            return;
        }

        // Local pool priority
        if (_connection is Connection owner)
        {
            owner.ReturnEventArgs(this);
            return;
        }

        if (_connection is WebSocketConnection webSocketOwner)
        {
            webSocketOwner.ReturnEventArgs(this);
            return;
        }

        if (_connection is DatagramConnection datagramOwner)
        {
            datagramOwner.ReturnEventArgs(this);
            return;
        }
    }

    #endregion APIs
}
